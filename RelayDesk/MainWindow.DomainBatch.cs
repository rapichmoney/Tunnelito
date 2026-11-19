using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RelayDesk;
public partial class MainWindow
{
    int rulesPage;
    Button? rulesPrevious,rulesNext;
    TextBlock? rulesPageLabel;
    void InitializeRuleTools()
    {
        var actions=new WrapPanel{Margin=new Thickness(0,0,0,12)};
        void Add(string text,RoutedEventHandler handler){var b=new Button{Content=text,Margin=new Thickness(0,0,8,8)};b.Click+=handler;actions.Children.Add(b);}
        Add("Домены из буфера…",PasteDomains);Add("Импорт списка…",ImportDomainFile);Add("Экспорт доменов…",ExportDomains);
        AppsPage.Children.Insert(Math.Min(2,AppsPage.Children.Count),actions);
        var pages=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,12)};
        rulesPrevious=new Button{Content="Предыдущие",Margin=new Thickness(0,0,12,0)};rulesNext=new Button{Content="Следующие"};rulesPageLabel=new TextBlock{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};
        rulesPrevious.Click+=(_,_)=>{rulesPage--;RefreshRoutes();};rulesNext.Click+=(_,_)=>{rulesPage++;RefreshRoutes();};
        pages.Children.Add(rulesPrevious);pages.Children.Add(rulesPageLabel);pages.Children.Add(rulesNext);AppsPage.Children.Add(pages);
        var repair=new StackPanel{Margin=new Thickness(0,20,0,12)};
        repair.Children.Add(new TextBlock{Text="Восстановление ядер",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
        repair.Children.Add(new TextBlock{Text="Заново загрузить sing-box, Xray, mihomo и zapret в версиях из установщика Tunnelito. Старые загрузки ядер Tunnelito будут удалены после проверки. Профили, подписки и правила сохранятся.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        var button=new Button{Content="Переустановить ядра Tunnelito…",HorizontalAlignment=HorizontalAlignment.Left};repair.Children.Add(button);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};repair.Children.Add(status);
        var updatesHeading=SettingsPage.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Text=="Обновления");
        SettingsPage.Children.Insert(updatesHeading==null?SettingsPage.Children.Count:SettingsPage.Children.IndexOf(updatesHeading),repair);
        button.Click+=async(_,_)=>
        {
            if(MessageBox.Show(this,"Будут загружены четыре ядра из официальных источников с проверкой SHA-256. После проверки Tunnelito кратко отключит свои подключения, заменит ядра и подключится снова. Старые загрузки ядер будут удалены. Другие приложения не затрагиваются.\n\nПродолжить?","Переустановка ядер",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            button.IsEnabled=false;
            try{await backend.ModernRepairCores(text=>status.Text=text);LoadZapretStrategies();RefreshFromBackend();}
            catch(Exception ex){status.Text="Не удалось завершить: "+ProgramJournal.Clean(ex.Message);}
            finally{button.IsEnabled=true;}
        };
    }
    async void PasteDomains(object sender,RoutedEventArgs e)
    {
        try{var clipboard=await ClipboardContent.Read();OpenDomainBatch(clipboard.Text);}
        catch(Exception ex){Toast(ex.Message);}
    }
    void OpenDomainBatch(string text)
    {
        if(rulesDialogOpen||backend.ModernBusy){Toast("Дождись текущей операции.");return;}
        WakeQuietMode();rulesDialogOpen=true;
        try
        {
            var window=new DomainBatchWindow(text,state.Rules){Owner=this};
            if(window.ShowDialog()!=true)return;
            var rules=window.Chosen.Select(r=>new RouteRule{Kind=r.Kind,Value=r.Value,Name=r.Value,Route=r.Route}).ToList();
            int index=window.First?0:state.Rules.Count-1;
            foreach(var rule in rules)state.Rules.Insert(index++,rule);
            if(PersistRules())Toast($"Добавлено доменов: {rules.Count}. Для текущего подключения нажми «Применить правила».");
        }
        catch(Exception ex){LoadProfileRules();Toast(ex.Message);}
        finally{rulesDialogOpen=false;RegisterActivity();}
    }
    void ImportDomainFile(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="Списки доменов (*.txt;*.json)|*.txt;*.json",Title="Импорт доменов"};
        try{if(dialog.ShowDialog(this)==true){if(new FileInfo(dialog.FileName).Length>4_000_000)throw new InvalidDataException("Список превышает 4 МБ.");OpenDomainBatch(File.ReadAllText(dialog.FileName));}}
        catch(Exception ex){Toast(ex.Message);}
    }
    void ExportDomains(object sender,RoutedEventArgs e)
    {
        var entries=state.Rules.Where(r=>DomainRuleTransfer.IsDomain(r.Kind)).Select(r=>new DomainEntry(r.Kind,r.Value,r.Route)).ToArray();
        if(entries.Length==0){Toast("Доменных правил пока нет.");return;}
        var dialog=new SaveFileDialog{Filter="Tunnelito JSON (*.json)|*.json|Текстовый список (*.txt)|*.txt",FileName="Tunnelito-domains",Title="Экспорт доменных правил",AddExtension=true};
        try{if(dialog.ShowDialog(this)==true){File.WriteAllText(dialog.FileName,DomainRuleTransfer.Export(entries,dialog.FilterIndex==1));Toast($"Экспортировано доменных правил: {entries.Length}.");}}
        catch(Exception ex){Toast(ex.Message);}
    }
}
