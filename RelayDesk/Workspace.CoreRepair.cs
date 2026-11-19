using System.IO;
using System.Text.Json;
using System.Diagnostics;

namespace RelayDesk;
public partial class Workspace
{
    async Task CheckRepairProxy(string id,string executable,Profile? probe=null)
    {
        var scratch=new Store(Path.Combine(store.Folder,"components","validation-"+Guid.NewGuid().ToString("N")));
        scratch.Settings.Engines[id]=executable;
        // A package self-test must not inherit routes, presets or unfinished user profiles.
        await new EngineSession(scratch,Write).Check(probe??CoreRepair.Probe(id));
    }
    async Task CheckRepairZapret(string executable)
    {
        var settings=new Preferences{ZapretExe=executable,ZapretArgs=FlowsealStrategies.Load(executable)[0].Arguments};
        foreach(var mode in new[]{ConnectionMode.ZapretOnly,ConnectionMode.Combined})
        {
            var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--dry-run");
            foreach(string argument in RuntimePolicy.ZapretArguments(settings,mode))info.ArgumentList.Add(argument);
            using var process=Process.Start(info)??throw new InvalidOperationException("Не удалось проверить zapret.");
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{await process.WaitForExitAsync(timeout.Token);if(process.ExitCode!=0)throw new InvalidOperationException("Проверка zapret: "+ProgramJournal.Clean(await stdout+await stderr));}
            finally{if(!process.HasExited)process.Kill(true);}
        }
    }
    public async Task ModernRepairCores(Action<string> progress)
    {
        if(ModernBusy||updatesBusy||HasChanges())throw new InvalidOperationException("Сохрани изменения и дождись текущей операции.");
        var originals=CoreRepair.ManagedInstalls(store.Folder);
        var backup=Path.Combine(store.Folder,"backups","before-core-repair-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        foreach(string name in new[]{"settings.json","profiles.json"})if(File.Exists(Path.Combine(store.Folder,name)))File.Copy(Path.Combine(store.Folder,name),Path.Combine(backup,name));
        var old=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(store.Settings))!;
        var active=session.ActiveProfile;bool vpn=session.ProxyRunning,zapret=session.ZapretRunning,stopped=false,committed=false;
        string activeConfig=session.ActiveRuntimeConfig,phase="Подготовка ядер";
        updatesBusy=installingUpdate=true;RefreshUpdateButtons();
        try
        {
            var checker=new ComponentUpdates(store);var prepared=new List<(ComponentUpdate Update,string Exe)>();
            foreach(var update in CoreRepair.Plan())
            {
                phase="Загрузка и проверка: "+update.Id;progress(phase);
                string exe=await checker.PrepareApproved(update,progress,CancellationToken.None);
                if(update.Id=="zapret")await CheckRepairZapret(exe);
                else await CheckRepairProxy(update.Id,exe);
                prepared.Add((update,exe));
            }
            phase="Проверка работающего подключения";progress(phase);
            if(vpn&&active!=null)await CheckRepairProxy(active.Core,prepared.Single(p=>p.Update.Id==active.Core).Exe,new Profile{Core=active.Core,Config=activeConfig});
            if(zapret)await ValidateNewZapret(prepared.Single(p=>p.Update.Id=="zapret").Exe,CancellationToken.None);
            phase="Переключение подключения";
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
        catch(Exception failure)
        {
            if(stopped&&!committed)
            {
                await session.StopAll();store.Settings.Engines=old.Engines;store.Settings.ZapretExe=old.ZapretExe;store.Settings.ZapretArgs=old.ZapretArgs;store.Settings.StrategyList=old.StrategyList;store.Settings.ComponentInstalls=old.ComponentInstalls;store.Save();
                try{if(vpn&&active!=null)await session.Connect(active,zapret);else if(zapret)await session.StartZapret();}catch(Exception ex){Write("Прежнее подключение не восстановлено: "+ex.Message);}
            }
            throw new InvalidOperationException(phase+": "+failure.Message,failure);
        }
        finally
        {
            SingExe.Text=store.Settings.Engines.GetValueOrDefault("sing-box","");XrayExe.Text=store.Settings.Engines.GetValueOrDefault("Xray","");MihomoExe.Text=store.Settings.Engines.GetValueOrDefault("mihomo","");ZapretExe.Text=store.Settings.ZapretExe;ZapretArgs.Text=store.Settings.ZapretArgs;StrategyList.Text=store.Settings.StrategyList;
            updatesBusy=installingUpdate=false;RefreshUpdateButtons();RefreshSetup();UpdateStatus();
        }
    }
}
