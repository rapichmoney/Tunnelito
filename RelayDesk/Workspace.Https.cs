namespace RelayDesk;
public partial class Workspace
{
    public async Task ModernSetHttpsInspection(bool enabled,IEnumerable<string> exclusions)
    {
        if(ModernBusy||HasChanges())throw new InvalidOperationException("Сохрани изменения и дождись текущей операции.");
        if(enabled&&session.ProxyRunning&&session.ActiveProfile?.Core!="sing-box")throw new InvalidOperationException("Выбери sing-box для HTTPS-перехвата.");
        if(enabled&&!HttpsCertificate.Trusted(store.Folder))throw new InvalidOperationException("Сначала установи сертификат Tunnelito.");
        var old=store.Settings.HttpsInspection;var oldExcluded=store.Settings.HttpsExcludedDomains;
        var next=exclusions.Select(s=>RouteRules.Normalize("domain_suffix",s)).Distinct().ToList();
        try
        {
            store.Settings.HttpsInspection=enabled;store.Settings.HttpsExcludedDomains=next;store.Save();
            if(session.ProxyRunning&&session.ActiveProfile!=null)await session.Reconfigure(SharedRouting.Copy(session.ActiveProfile));
        }
        catch{store.Settings.HttpsInspection=old;store.Settings.HttpsExcludedDomains=oldExcluded;store.Save();throw;}
    }
}
