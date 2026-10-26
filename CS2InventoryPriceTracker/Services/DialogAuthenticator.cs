using SteamKit2.Authentication;

namespace CS2InventoryPriceTracker.Services;

/// <summary>Asks the user for Steam Guard confirmation or codes with app dialogs.</summary>
public class DialogAuthenticator(Page page, Action<string> showStatus) : IAuthenticator
{
    private const string ApproveOption = "Approve in the Steam app";
    private const string CodeOption = "Enter a code";

    public Task<bool> AcceptDeviceConfirmationAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var choice = await page.DisplayActionSheetAsync("Steam Guard", "Cancel", null, ApproveOption, CodeOption);
        if (choice == ApproveOption)
        {
            showStatus("Open the Steam app on your phone and approve the login…");
            return true;
        }
        if (choice == CodeOption)
            return false;

        throw new OperationCanceledException();
    });

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => AskCodeAsync(
        previousCodeWasIncorrect
            ? "That code was wrong. Enter the current code from the Steam app (Steam Guard)."
            : "Enter the code shown in the Steam app (Steam Guard).");

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => AskCodeAsync(
        previousCodeWasIncorrect
            ? $"That code was wrong. Enter the latest code Steam sent to {email}."
            : $"Enter the code Steam sent to {email}.");

    private Task<string> AskCodeAsync(string message) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var code = await page.DisplayPromptAsync("Steam Guard", message, "OK", "Cancel",
            placeholder: "Code", maxLength: 10, keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(code))
            throw new OperationCanceledException();

        showStatus("Logging in…");
        return code.Trim().ToUpperInvariant();
    });
}
