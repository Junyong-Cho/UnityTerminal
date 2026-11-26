using UnityEngine;

namespace UnityTerminal
{
    // Decides how the terminal toolbar fits the window width. Pure arithmetic, so it can be checked
    // without drawing: the window measures text widths and passes them in.
    // Clear/Stop/Restart always keep their width. When the full toolbar does not fit, the font size
    // slider and AutoScroll collapse into a View menu and the shell popup shrinks. Status and
    // directory labels are drawn shrinkable, so they give up space before any button does.
    internal struct TerminalToolbarLayout
    {
        public const float ShellPopupWidth = 165, MinShellPopupWidth = 80;
        public const float FontLabelWidth = 55, FontSliderWidth = 130, Gap = 10;
        public const float ClearWidth = 50, StopWidth = 45, RestartWidth = 60;
        // Space the full layout keeps for the status/directory labels before collapsing.
        public const float LabelReserve = 60;
        // Toolbar edge padding and item margins.
        public const float Padding = 12;

        public bool CollapseSettings;
        public float ShellWidth;

        public static float ButtonsWidth(bool showStop)
        {
            return ClearWidth + (showStop == true ? StopWidth : 0) + RestartWidth;
        }

        public static TerminalToolbarLayout Compute(float windowWidth, float autoScrollWidth, float viewMenuWidth, bool showStop)
        {
            float available = windowWidth - Padding;
            float buttons = ButtonsWidth(showStop);
            float full = ShellPopupWidth + Gap + FontLabelWidth + FontSliderWidth + Gap + autoScrollWidth + LabelReserve + Gap + buttons;
            if (full <= available) return new TerminalToolbarLayout { CollapseSettings = false, ShellWidth = ShellPopupWidth };
            float shell = available - viewMenuWidth - buttons - LabelReserve;
            shell = Mathf.Clamp(shell, MinShellPopupWidth, ShellPopupWidth);
            return new TerminalToolbarLayout { CollapseSettings = true, ShellWidth = shell };
        }

        // Width taken by items that never shrink; must not exceed windowWidth - Padding.
        public float FixedWidth(float autoScrollWidth, float viewMenuWidth, bool showStop)
        {
            float buttons = ButtonsWidth(showStop);
            if (CollapseSettings == true) return ShellWidth + viewMenuWidth + buttons;
            return ShellWidth + Gap + FontLabelWidth + FontSliderWidth + Gap + autoScrollWidth + Gap + buttons;
        }
    }
}
