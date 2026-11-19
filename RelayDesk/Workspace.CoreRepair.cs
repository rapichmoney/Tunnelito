using System.IO;
using System.Text.Json;

namespace RelayDesk;
public partial class Workspace
{
    public async Task ModernRepairCores(Action<string> progress)
    {
        if(ModernBusy||updatesBusy||HasChanges())throw new InvalidOperationException("Сохрани изменения и дождись текущей операции.");
        var originals=CoreRepair.ManagedInstalls(store.Folder);
        var backup=Path.Combine(store.Folder,"backups","before-core-repair-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        foreach(string name in new[]{"settings.json","profiles.json"})if(File.Exists(Path.Combine(store.Folder,name)))File.Copy(Path.Combine(store.Folder,name),Path.Combine(backup,name));
        var old=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(store.Settings))!;
        var active=session.ActiveProfile;bool vpn=session.ProxyRunning,zapret=session.ZapretRunning,stopped=false,committed=false;
        updatesBusy=installingUpdate=true;RefreshUpdateButtons();
        try
        {
            var checker=new ComponentUpdates(store);var prepared=new List<(ComponentUpdate Update,string Exe)>();
            foreach(var update in CoreRepair.Plan())
            {
                progress("Загрузка и проверка: "+update.Id);
                string exe=await checker.PrepareApproved(update,progress,CancellationToken.None);
                if(update.Id=="sing-box")await ValidateNewSingBox(exe,CancellationToken.None);
                else if(update.Id=="zapret")await ValidateNewZapret(exe,CancellationToken.None);
                else await ValidateNewProxyCore(update.Id,exe,CancellationToken.None);
                prepared.Add((update,exe));
            }
            progress("Ядра проверены. Переключаю подключение…");CancelWindowsStartup();await session.StopAll();stopped=true;
            foreach(var item in prepared)
            {
                string previous=item.Update.Id=="zapret"?old.ZapretExe:old.Engines.GetValueOrDefault(item.Update.Id,"");
                if(item.Update.Id=="zapret")
                {
                    store.Settings.ZapretArgs=CoreRepair.RebindZapret(old.ZapretArgs,previous,item.Exe);store.Settings.StrategyList=CoreRepair.RebindZapret(old.StrategyList,previous,item.Exe);
                    if(string.IsNullOrWhiteSpace(store.Settings.ZapretArgs)){var strategies=FlowsealStrategies.Load(item.Exe);store.Settings.ZapretArgs=strategies[0].Arguments;store.Settings.StrategyList=FlowsealStrategies.Format(strategies);}
                    store.Settings.ZapretExe=item.Exe;
                }
                else store.Settings.Engines[item.Update.Id]=item.Exe;
                store.Settings.ComponentInstalls[item.Update.Id]=new(item.Update.Revision,item.Exe,"",DateTimeOffset.UtcNow);
            }
            store.Save();
            if(vpn&&active!=null)await session.Connect(active,zapret);else if(zapret)await session.StartZapret();
            committed=true;
            PendingUpdates.CancelCores(store);
            int retained=0;foreach(string path in originals)try{CoreRepair.DeleteManagedInstall(store.Folder,path);}catch(IOException){retained++;}catch(UnauthorizedAccessException){retained++;}
            progress(retained==0?"Четыре ядра переустановлены. Профили и правила сохранены; прежнее состояние подключения восстановлено.":$"Ядра переустановлены. Не удалось удалить занятые старые папки: {retained}. Новые ядра уже выбраны.");
        }
        catch
        {
            if(stopped&&!committed)
            {
                await session.StopAll();store.Settings.Engines=old.Engines;store.Settings.ZapretExe=old.ZapretExe;store.Settings.ZapretArgs=old.ZapretArgs;store.Settings.StrategyList=old.StrategyList;store.Settings.ComponentInstalls=old.ComponentInstalls;store.Save();
                try{if(vpn&&active!=null)await session.Connect(active,zapret);else if(zapret)await session.StartZapret();}catch(Exception ex){Write("Прежнее подключение не восстановлено: "+ex.Message);}
            }
            throw;
        }
        finally
        {
            SingExe.Text=store.Settings.Engines.GetValueOrDefault("sing-box","");XrayExe.Text=store.Settings.Engines.GetValueOrDefault("Xray","");MihomoExe.Text=store.Settings.Engines.GetValueOrDefault("mihomo","");ZapretExe.Text=store.Settings.ZapretExe;ZapretArgs.Text=store.Settings.ZapretArgs;StrategyList.Text=store.Settings.StrategyList;
            updatesBusy=installingUpdate=false;RefreshUpdateButtons();RefreshSetup();UpdateStatus();
        }
    }
}
