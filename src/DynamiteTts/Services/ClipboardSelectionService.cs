using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DynamiteTts.Native;

namespace DynamiteTts.Services;

public class ClipboardSelectionService
{
    public async Task<string?> CaptureSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        // 1. Snapshot current clipboard on STA thread
        IDataObject? previousData = null;
        try
        {
            await RunOnStaThreadAsync(() =>
            {
                try
                {
                    previousData = Clipboard.GetDataObject();
                }
                catch
                {
                    // Non-fatal if clipboard is locked by another app
                }
            });
        }
        catch
        {
            // Ignore
        }

        var initialSeq = NativeMethods.GetClipboardSequenceNumber();

        // 2. Send Clean Ctrl+C with temporary modifier suppression
        SendCleanCtrlC();

        // 3. Poll for clipboard update with 10ms resolution
        var text = await PollForNewTextAsync(initialSeq, timeoutMs: 800, cancellationToken);

        // 4. Asynchronously restore previous clipboard content after short delay
        if (previousData != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(350, CancellationToken.None);
                    await RunOnStaThreadAsync(() =>
                    {
                        try
                        {
                            Clipboard.SetDataObject(previousData, true);
                        }
                        catch
                        {
                            // Transient clipboard lock, non-fatal
                        }
                    });
                }
                catch
                {
                    // Ignore restore errors
                }
            });
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        return TextSanitizer.Sanitize(text);
    }

    private static void SendCleanCtrlC()
    {
        var isShiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        var isAltDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        var isWinDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

        var inputList = new List<NativeMethods.INPUT>();

        // Temporarily lift modifiers so target app sees pure Ctrl+C instead of Ctrl+Shift+C / Alt+C
        if (isShiftDown)
        {
            inputList.Add(CreateKeyInput(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP));
        }
        if (isAltDown)
        {
            inputList.Add(CreateKeyInput(NativeMethods.VK_MENU, NativeMethods.KEYEVENTF_KEYUP));
        }
        if (isWinDown)
        {
            inputList.Add(CreateKeyInput(NativeMethods.VK_LWIN, NativeMethods.KEYEVENTF_KEYUP));
            inputList.Add(CreateKeyInput(NativeMethods.VK_RWIN, NativeMethods.KEYEVENTF_KEYUP));
        }

        // Ctrl Down
        inputList.Add(CreateKeyInput(NativeMethods.VK_CONTROL, 0));
        // C Down
        inputList.Add(CreateKeyInput(NativeMethods.VK_C, 0));
        // C Up
        inputList.Add(CreateKeyInput(NativeMethods.VK_C, NativeMethods.KEYEVENTF_KEYUP));
        // Ctrl Up
        inputList.Add(CreateKeyInput(NativeMethods.VK_CONTROL, NativeMethods.KEYEVENTF_KEYUP));

        var inputs = inputList.ToArray();
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT CreateKeyInput(ushort vk, uint flags)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD
        };
        input.u.ki.wVk = vk;
        input.u.ki.dwFlags = flags;
        return input;
    }

    private static async Task<string?> PollForNewTextAsync(uint initialSeq, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, cancellationToken);

            var currentSeq = NativeMethods.GetClipboardSequenceNumber();
            if (currentSeq != initialSeq)
            {
                string? text = null;
                await RunOnStaThreadAsync(() =>
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            text = Clipboard.GetText();
                        }
                    }
                    catch
                    {
                        // Retry on next cycle if locked
                    }
                });

                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
