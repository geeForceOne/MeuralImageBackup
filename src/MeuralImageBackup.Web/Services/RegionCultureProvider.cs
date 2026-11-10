using MeuralImageBackup.Core.Services;
using Microsoft.AspNetCore.Localization;

namespace MeuralImageBackup.Web.Services;

// The region format chosen on the Settings page; when it's automatic, the request falls through to
// the browser's Accept-Language. The UI itself is always US English. The interactive circuit keeps
// the culture of the request that opened it, so saving a new format reloads the page.
public sealed class RegionCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext http)
    {
        var (region, _) = await http.RequestServices.GetRequiredService<RegionSettings>().GetAsync(http.RequestAborted);
        return region is null ? null : new ProviderCultureResult(region, "en-US");
    }
}
