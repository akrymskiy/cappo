namespace CapPo.App;

/// <param name="IsUsable">False once a Store trial has ended: CapPo then shows status but doesn't open sign-in pages.</param>
/// <param name="TrialRemaining">For a Store trial: time left.</param>
public sealed record LicenseInfo(bool IsUsable, bool IsTrial, TimeSpan? TrialRemaining)
{
    public static readonly LicenseInfo Full = new(true, false, null);
}

/// <summary>
/// The Store build's license and trial (Windows.Services.Store). The GitHub build has none of this: it's always
/// <see cref="LicenseInfo.Full"/>, with no checks, reminders or limits.
/// </summary>
internal static class StoreLicense
{
#if STORE_BUILD
    private static Windows.Services.Store.StoreContext? s_context;

    /// <summary>The license changed, e.g. after buying, or a trial ended.</summary>
    public static event Action? Changed;

    private static Windows.Services.Store.StoreContext Context
    {
        get
        {
            if (s_context is null)
            {
                s_context = Windows.Services.Store.StoreContext.GetDefault();
                s_context.OfflineLicensesChanged += (_, _) => Changed?.Invoke();
            }
            return s_context;
        }
    }

    public static async Task<LicenseInfo> GetAsync()
    {
        try
        {
            var license = await Context.GetAppLicenseAsync();
            Log.Info($"Store license: active {license.IsActive}, trial {license.IsTrial}, expires {license.ExpirationDate:u}");
#if DEBUG
            // Sideloaded debug builds have no Store license; don't lock them out.
            if (!license.IsActive)
                return LicenseInfo.Full;
#endif
            if (!license.IsActive)
                return new LicenseInfo(false, license.IsTrial, TimeSpan.Zero);
            return license.IsTrial
                ? new LicenseInfo(true, true, license.ExpirationDate - DateTimeOffset.Now)
                : LicenseInfo.Full;
        }
        catch (Exception ex)
        {
            // Never punish a paying user for the Store being unreachable: carry on as licensed.
            Log.Error(ex, "Couldn't read the Store license");
            return LicenseInfo.Full;
        }
    }

    /// <summary>Opens the Store's purchase dialog for CapPo.</summary>
    /// <param name="window">The window to show the dialog over.</param>
    public static async Task<bool> PurchaseAsync(nint window)
    {
        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(Context, window);
            var product = await Context.GetStoreProductForCurrentAppAsync();
            if (product.Product is null)
                return false;
            var result = await product.Product.RequestPurchaseAsync();
            Changed?.Invoke();
            return result.Status is Windows.Services.Store.StorePurchaseStatus.Succeeded
                or Windows.Services.Store.StorePurchaseStatus.AlreadyPurchased;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Purchase failed");
            return false;
        }
    }
#else
    public static Task<LicenseInfo> GetAsync() => Task.FromResult(LicenseInfo.Full);
#endif
}
