using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace SqlVersionControl.Services;

public static class DialogExtensions
{
    /// <summary>
    /// Shows a dialog as a separate top-level window (no owner) on macOS,
    /// manually centered on the logical owner. Avoids parent-child window
    /// relationship while keeping visual centering. Non-modal on macOS,
    /// falls back to normal ShowDialog on other platforms.
    /// </summary>
    public static Task ShowDialogDetached(this Window dialog, Window logicalOwner)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var pos = logicalOwner.Position;
            var ownerW = logicalOwner.Bounds.Width;
            var ownerH = logicalOwner.Bounds.Height;

            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Opened += (_, _) =>
            {
                var x = pos.X + (int)((ownerW - dialog.Bounds.Width) / 2);
                var y = pos.Y + (int)((ownerH - dialog.Bounds.Height) / 2);
                dialog.Position = new PixelPoint(Math.Max(0, x), Math.Max(0, y));
            };

            dialog.Show();
            return WaitForClose(dialog);
        }

        return dialog.ShowDialog(logicalOwner);
    }

    private static Task WaitForClose(Window window)
    {
        var tcs = new TaskCompletionSource();
        window.Closed += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }
}
