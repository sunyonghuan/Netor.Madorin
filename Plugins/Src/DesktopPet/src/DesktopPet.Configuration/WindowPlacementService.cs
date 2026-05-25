using DesktopPet.Abstractions;

namespace DesktopPet.Configuration;

public static class WindowPlacementService
{
    public static PetWindowPlacement ResolveStartupPlacement(
        PetWindowPlacement savedPlacement,
        IReadOnlyList<DisplayInfo> displays,
        int defaultWidth = 420,
        int defaultHeight = 560)
    {
        if (displays.Count == 0)
        {
            return savedPlacement with
            {
                Width = NormalizeSize(savedPlacement.Width, defaultWidth),
                Height = NormalizeSize(savedPlacement.Height, defaultHeight)
            };
        }

        var width = NormalizeSize(savedPlacement.Width, defaultWidth);
        var height = NormalizeSize(savedPlacement.Height, defaultHeight);

        if (savedPlacement.RememberPlacement && TryFindDisplay(savedPlacement, displays, out var savedDisplay))
        {
            var restored = savedPlacement with
            {
                Width = width,
                Height = height
            };

            if (IsVisibleOnDisplay(restored, savedDisplay))
            {
                return restored;
            }
        }

        var fallbackDisplay = displays.FirstOrDefault(display => display.IsPrimary);
        if (fallbackDisplay.Width == 0 && fallbackDisplay.Height == 0)
        {
            fallbackDisplay = displays[0];
        }

        return savedPlacement with
        {
            MonitorDeviceName = fallbackDisplay.DeviceName,
            X = fallbackDisplay.X + Math.Max(0, fallbackDisplay.Width - width - 24),
            Y = fallbackDisplay.Y + Math.Max(0, fallbackDisplay.Height - height - 48),
            Width = width,
            Height = height
        };
    }

    private static bool TryFindDisplay(
        PetWindowPlacement placement,
        IReadOnlyList<DisplayInfo> displays,
        out DisplayInfo display)
    {
        if (!string.IsNullOrWhiteSpace(placement.MonitorDeviceName))
        {
            foreach (var candidate in displays)
            {
                if (string.Equals(
                    candidate.DeviceName,
                    placement.MonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    display = candidate;
                    return true;
                }
            }
        }

        foreach (var candidate in displays)
        {
            if (IsVisibleOnDisplay(placement, candidate))
            {
                display = candidate;
                return true;
            }
        }

        display = default;
        return false;
    }

    private static bool IsVisibleOnDisplay(PetWindowPlacement placement, DisplayInfo display)
    {
        var right = placement.X + Math.Max(1, placement.Width);
        var bottom = placement.Y + Math.Max(1, placement.Height);
        var displayRight = display.X + display.Width;
        var displayBottom = display.Y + display.Height;

        return right > display.X
            && placement.X < displayRight
            && bottom > display.Y
            && placement.Y < displayBottom;
    }

    private static int NormalizeSize(int value, int fallback)
    {
        if (value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, 120, 2400);
    }
}
