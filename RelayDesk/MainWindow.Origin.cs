using System.Windows;
namespace RelayDesk;
public partial class MainWindow
{
    bool locatingOrigin;
    DateTimeOffset lastOriginAttempt;
    OriginLocation? originLocation;
    string originMessage="Положение определяется по прямому подключению, без VPN.";
    async Task RefreshOrigin(bool force=false)
    {
        if(legacyUi||locatingOrigin||!force&&DateTimeOffset.UtcNow-lastOriginAttempt<TimeSpan.FromMinutes(1))return;
        try
        {
            if(Environment.GetEnvironmentVariable("RELAYDESK_DATA_DIR")!=null&&!force){originLocation=backend.ModernStore.Settings.OriginLocation;RefreshOrbit();return;}
            originLocation=null;var network=DirectNetwork.Interface();var previous=backend.ModernStore.Settings.OriginLocation;
            originLocation=previous?.NetworkKey==OriginLocator.NetworkKey(network)?previous:null;
            if(!force&&previous!=null&&previous.NetworkKey==OriginLocator.NetworkKey(network)&&DateTimeOffset.UtcNow-previous.DetectedAt<TimeSpan.FromHours(1))return;
            locatingOrigin=true;lastOriginAttempt=DateTimeOffset.UtcNow;originMessage="Определяем исходное подключение…";RefreshOrbit();
            var location=await OriginLocator.Locate(network,lifetime.Token);originLocation=location;backend.ModernStore.Settings.OriginLocation=location;Save();
            originMessage="Данные IP провайдера. Для обновления нажми на карточку zapret.";
        }
        catch(OperationCanceledException){}
        catch(Exception) {originMessage="Не удалось определить прямое подключение. Нажми на карточку zapret, чтобы повторить.";}
        finally {locatingOrigin=false;if(!leaving)RefreshOrbit();}
    }
}
