using System;
using System.Diagnostics;
using System.IO;
using nanoGate.Core;

namespace nanoGate.Points
{
    // 파일 접근 추상화. 게이트웨이(GatewaySettings 등)에서도 사용하므로 public입니다.
    public class PointMappingFileSystem
    {
        public virtual bool Exists(string path) { return File.Exists(path); }
        public virtual void Delete(string path) { File.Delete(path); }
        public virtual void Move(string source, string destination) { File.Move(source, destination); }
        public virtual Stream Open(string path, FileMode mode, FileAccess access)
        {
            return new FileStream(path, mode, access);
        }
    }

    public static class PointMappingStore
    {
        // const 유지 필수 (PointMapping.SchemaHeader와 동일 이유).
        internal const string EmptyTable = PointMappingTable.SchemaHeader + "\n";
        // 프로필 키에 따라 달라지므로 상수가 아닌 동적 경로 참조.
        internal static string FilePath { get { return nanoGate.Core.DeviceStorage.PointsFilePath; } }
        internal static string TempFilePath { get { return nanoGate.Core.DeviceStorage.PointsTempFilePath; } }
        internal static string BackupFilePath { get { return nanoGate.Core.DeviceStorage.PointsBackupFilePath; } }
        public static readonly PointMappingFileSystem FileSystem = new PointMappingFileSystem();

        // 남아 있는 백업이 기준이며, 백업을 삭제하는 순간이 교체의 커밋 지점입니다.
        internal static void RestoreBackup(PointMappingFileSystem fileSystem)
        {
            if (!fileSystem.Exists(BackupFilePath))
                return;
            if (fileSystem.Exists(FilePath))
                fileSystem.Delete(FilePath);
            fileSystem.Move(BackupFilePath, FilePath);
        }

        public static string DeleteAll()
        {
            return nanoGate.Core.DeviceStorage.DeletePointFiles();
        }

        public static string DeleteCurrent()
        {
            return nanoGate.Core.DeviceStorage.DeleteCurrentPointFiles();
        }

        // 아직 저장된 표가 없으면 error도 null로 반환합니다.
        public static Stream OpenRead(out string error)
        {
            return OpenRead(FileSystem, out error);
        }

        internal static Stream OpenRead(PointMappingFileSystem fileSystem, out string error)
        {
            error = null;
            Stream file = null;
            try
            {
                string path = fileSystem.Exists(BackupFilePath) ? BackupFilePath : FilePath;
                if (!fileSystem.Exists(path))
                    return null;

                file = fileSystem.Open(path, FileMode.Open, FileAccess.Read);
                if (file.Length > PointMappingTable.MaximumFileLength)
                {
                    file.Dispose();
                    error = "포인트 매핑 파일이 " + PointMappingTable.MaximumFileLength + "바이트를 넘습니다.";
                    return null;
                }

                return file;
            }
            catch (Exception storageError)
            {
                if (file != null)
                    file.Dispose();
                error = ErrorText.Describe(storageError);
                return null;
            }
        }
    }

    // 검증된 업로드를 임시 파일로 스트리밍하고, 성공한 뒤에만 표를 교체합니다.
    internal sealed class PointMappingUpload : IDisposable
    {
        private readonly PointMappingFileSystem _fileSystem;
        private Stream _file;
        private PointMappingStreamParser _parser;
        private bool _committed;
        private int _count;

        public PointMappingUpload() : this(PointMappingStore.FileSystem) { }

        internal PointMappingUpload(PointMappingFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public bool StorageFailure { get; private set; }

        public int Count
        {
            get { return _parser == null ? _count : _parser.Count; }
        }

        public string Begin()
        {
            try
            {
                PointMappingStore.RestoreBackup(_fileSystem);
                if (_fileSystem.Exists(PointMappingStore.TempFilePath))
                    _fileSystem.Delete(PointMappingStore.TempFilePath);

                _file = _fileSystem.Open(PointMappingStore.TempFilePath, FileMode.Create, FileAccess.Write);
                _parser = new PointMappingStreamParser(_file);
                return null;
            }
            catch (Exception storageError)
            {
                StorageFailure = true;
                return ErrorText.Describe(storageError);
            }
        }

        public string Write(byte[] buffer, int count)
        {
            try
            {
                return _parser.Write(buffer, 0, count);
            }
            catch (Exception storageError)
            {
                StorageFailure = true;
                return ErrorText.Describe(storageError);
            }
        }

        public string Commit()
        {
            try
            {
                string error = _parser.Complete();
                if (error != null)
                    return error;

                _count = _parser.Count;
                _parser = null;
                _file.Flush();
                _file.Dispose();
                _file = null;
                if (_fileSystem.Exists(PointMappingStore.FilePath))
                    _fileSystem.Move(PointMappingStore.FilePath, PointMappingStore.BackupFilePath);

                _fileSystem.Move(PointMappingStore.TempFilePath, PointMappingStore.FilePath);
                if (_fileSystem.Exists(PointMappingStore.BackupFilePath))
                    _fileSystem.Delete(PointMappingStore.BackupFilePath);
                _committed = true;
                return null;
            }
            catch (Exception storageError)
            {
                StorageFailure = true;
                string error = ErrorText.Describe(storageError);
                try
                {
                    PointMappingStore.RestoreBackup(_fileSystem);
                }
                catch (Exception recoveryError)
                {
                    // 이름을 복원할 수 없더라도 OpenRead는 백업을 계속 제공합니다.
                    error += " / 기존 매핑 복구 실패(백업 유지): " + ErrorText.Describe(recoveryError);
                }

                return error;
            }
        }

        public void Dispose()
        {
            if (_parser != null)
            {
                _count = _parser.Count;
                _parser = null;
            }
            if (_file != null)
            {
                try
                {
                    _file.Dispose();
                }
                catch (Exception storageError)
                {
                    StorageFailure = true;
                    Debug.WriteLine("Closing point mapping upload failed: " + ErrorText.Describe(storageError));
                }

                _file = null;
            }

            if (_committed)
                return;

            try
            {
                if (_fileSystem.Exists(PointMappingStore.TempFilePath))
                    _fileSystem.Delete(PointMappingStore.TempFilePath);
            }
            catch (Exception storageError)
            {
                StorageFailure = true;
                Debug.WriteLine("Removing point mapping upload failed: " + ErrorText.Describe(storageError));
            }
        }
    }
}
