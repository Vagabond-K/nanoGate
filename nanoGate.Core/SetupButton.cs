namespace nanoGate.Core
{
    internal sealed class SetupButton
    {
        internal const int None = 0;
        internal const int Exit = 1;
        internal const int Enter = 2;
        internal const int FactoryReset = 3;
        private const int DebounceMilliseconds = 50;
        private bool _rawPressed;
        private bool _pressed;
        private long _changedAt;
        private long _pressedAt;

        internal int Update(bool pressed, long now, bool setup)
        {
            if (pressed != _rawPressed)
            {
                _rawPressed = pressed;
                _changedAt = now;
            }
            if (_rawPressed == _pressed || now - _changedAt < DebounceMilliseconds)
                return None;

            _pressed = _rawPressed;
            if (_pressed)
            {
                _pressedAt = _changedAt;
                return None;
            }

            // 디바운싱이 끝난 시점이 아니라 실제로 버튼이 놓인 표본 시점을 사용합니다.
            long held = _changedAt - _pressedAt;
            if (held >= 10000)
                return FactoryReset;
            if (held >= 3000)
                return setup ? None : Enter;
            return held >= 80 && setup ? Exit : None;
        }

        internal bool IsLit(long now, bool setup)
        {
            if (!_pressed)
                return setup && ((now / 800) & 1) != 0;
            long held = now - _pressedAt;
            return held < 3000 || ((now / (held < 10000 ? 400 : 100)) & 1) != 0;
        }
    }
}
