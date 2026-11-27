using UnityEngine;

namespace UnityTerminal
{
    // The IME can commit its text before Unity dispatches the physical Enter/Tab event.
    // Remember that transition across GUI events so the accepting key isn't also an app command.
    // Exception: the Korean IME commits the syllable and then delivers Enter as a normal key
    // (the committed text is already in the field), so after a Hangul commit Enter is not consumed
    // and one press commits and submits. Japanese/Chinese IMEs use Enter only to accept a
    // conversion, so for them the accepting Enter is still consumed.
    internal sealed class TerminalComposition
    {
        private bool previouslyActive;
        private bool activeBeforeControl;
        private bool commitPending;
        private bool lastCompositionHangul;

        public void BeginEvent() { activeBeforeControl = Sample(); }

        public void Reset() { previouslyActive = activeBeforeControl = commitPending = lastCompositionHangul = false; }

        public string Describe()
        {
            return "composition active=" + previouslyActive + " beforeControl=" + activeBeforeControl +
                " commitPending=" + commitPending + " lastHangul=" + lastCompositionHangul;
        }

        public bool ConsumesNavigation(Event e)
        {
            bool active = Sample();
            bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
            bool navigation = enter ||
                e.keyCode == KeyCode.Tab || e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Backspace ||
                e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow ||
                e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow ||
                e.keyCode == KeyCode.Home || e.keyCode == KeyCode.End;
            bool acceptingKey = commitPending && navigation;
            if (acceptingKey == true && enter == true && lastCompositionHangul == true) acceptingKey = false;
            bool consume = activeBeforeControl || active || acceptingKey;
            if ((e.type == EventType.KeyDown && e.keyCode != KeyCode.None) || e.type == EventType.MouseDown)
                commitPending = false;
            return consume;
        }

        private bool Sample()
        {
            string text = Input.compositionString;
            bool active = text.Length > 0;
            if (previouslyActive && !active) commitPending = true;
            if (active)
            {
                commitPending = false;
                lastCompositionHangul = ContainsHangul(text);
            }
            previouslyActive = active;
            return active;
        }

        // Hangul Jamo, Compatibility Jamo, Jamo Extended-A/B and precomposed syllables.
        internal static bool ContainsHangul(string text)
        {
            foreach (char c in text)
            {
                if ((c >= 'ᄀ' && c <= 'ᇿ') || (c >= '㄰' && c <= '㆏') ||
                    (c >= 'ꥠ' && c <= '꥿') || (c >= '가' && c <= '퟿'))
                    return true;
            }
            return false;
        }
    }
}
