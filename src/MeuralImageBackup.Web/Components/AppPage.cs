using MeuralImageBackup.Core.Services;
using MeuralImageBackup.Web.Services;
using Microsoft.AspNetCore.Components;

namespace MeuralImageBackup.Web.Components;

// Base for the pages: the shared dialogs and time zone, the library, and running an action with
// its problem shown as a toast.
public abstract class AppPage : ComponentBase, IDisposable
{
    [CascadingParameter]
    protected AppUi Ui { get; set; } = default!;

    [Inject]
    protected NavigationManager Nav { get; set; } = default!;

    [Inject]
    protected LibraryService LibraryService { get; set; } = default!;

    private readonly CancellationTokenSource _pageCts = new();

    // Canceled when the page is left.
    protected CancellationToken PageToken => _pageCts.Token;

    // True while RunAsync is working, to disable buttons.
    protected bool Busy { get; private set; }

    protected TimeZoneInfo Zone => Ui.Zone;

    // Runs an action; a problem (something deleted meanwhile, a file that can't be removed) shows as
    // a toast. Returns false when it didn't work.
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        if (Busy) return false;
        Busy = true;
        StateHasChanged();
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is NotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Toast.Error(ex.Message);
            return false;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    // The URL of this page with its filters, without the ones at their default.
    protected void ReplaceQuery(IReadOnlyDictionary<string, object?> query) =>
        Nav.NavigateTo(Nav.GetUriWithQueryParameters(query), replace: true);

    public virtual void Dispose()
    {
        _pageCts.Cancel();
        _pageCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
