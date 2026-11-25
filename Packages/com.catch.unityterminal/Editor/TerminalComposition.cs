using UnityEngine;

namespace UnityTerminal
{
    // The IME can commit its text before Unity dispatches the physical Enter/Tab event.
    // Remember that transition across GUI events so the accepting key isn't also an app command.
    internal sealed class TerminalComposition
    {
        private bool previouslyActive;
        private bool activeBeforeControl;
        private bool commitPending;

        public void BeginEvent() { activeBeforeControl = Sample(); }

        public void Reset() { previouslyActive = activeBeforeControl = commitPending = false; }

        public bool ConsumesNavigation(Event e)
        {
            bool active = Sample();
            bool navigation = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter ||
                e.keyCode == KeyCode.Tab || e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Backspace ||
                e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow ||
                e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow ||
                e.keyCode == KeyCode.Home || e.keyCode == KeyCode.End;
            bool consume = activeBeforeControl || active || (commitPending && navigation);
            if ((e.type == EventType.KeyDown && e.keyCode != KeyCode.None) || e.type == EventType.MouseDown)
                commitPending = false;
            return consume;
        }

        private bool Sample()
        {
            bool active = Input.compositionString.Length > 0;
            if (previouslyActive && !active) commitPending = true;
            if (active) commitPending = false;
            previouslyActive = active;
            return active;
        }
    }
}
