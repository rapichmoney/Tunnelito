using System.Windows;
using System.Windows.Controls;

namespace RelayDesk;
public partial class MainWindow
{
    TextBlock? httpsStatus;
    TextBox? httpsEvents;
    void InitializeHttpsSettings()
    {
        var panel=new StackPanel{Margin=new Thickness(0,20,0,12)};
        panel.Children.Add(new TextBlock{Text="Зависимые запросы HTTPS",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
        panel.Children.Add(new TextBlock{Text="Tunnelito расшифровывает HTTPS через локальный прокси. Если GET/HEAD от сайта с правилом VPN получил 403, один раз повторяет его через VPN. Успешный домен направляется через VPN на 30 минут для связанных запросов. Нужен Referer или Origin; полная цепочка инициаторов недоступна.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        panel.Children.Add(new TextBlock{Text="Только sing-box. При включении используется системный HTTP-прокси, в том числе вместе с TUN. Тела запросов, пароли и cookies не сохраняются. Браузеры со своим VPN/прокси и приложения с закреплёнными сертификатами могут не поддерживаться.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        var enable=new CheckBox{Content="Проверять 403 и временно подключать зависимости",IsChecked=backend.ModernStore.Settings.HttpsInspection,Margin=new Thickness(0,0,0,12)};panel.Children.Add(enable);
        panel.Children.Add(new TextBlock{Text="Не расшифровывать домены (по одному в строке)",Margin=new Thickness(0,0,0,6)});
        var excluded=new TextBox{AcceptsReturn=true,Height=74,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=string.Join('\n',backend.ModernStore.Settings.HttpsExcludedDomains)};panel.Children.Add(excluded);
        var apply=new Button{Content="Применить HTTPS-настройки",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,10,0,10)};panel.Children.Add(apply);
        httpsStatus=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};panel.Children.Add(httpsStatus);
        var actions=new WrapPanel();panel.Children.Add(actions);
        var install=new Button{Content="Установить сертификат",Margin=new Thickness(0,0,8,8)};var remove=new Button{Content="Удалить доверие",Margin=new Thickness(0,0,8,8)};var clear=new Button{Content="Очистить временные правила",Margin=new Thickness(0,0,8,8)};actions.Children.Add(install);actions.Children.Add(remove);actions.Children.Add(clear);
        httpsEvents=new TextBox{IsReadOnly=true,Height=110,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,8,0,0)};panel.Children.Add(httpsEvents);
        string certificateState(){try{return HttpsCertificate.Trusted(backend.ModernStore.Folder)?"Сертификат установлен для текущего пользователя Windows.":"Сертификат не установлен.";}catch{return "Не удалось проверить сертификат.";}}
        httpsStatus.Text=certificateState();
        apply.Click+=async(_,_)=>{apply.IsEnabled=false;try{await backend.ModernSetHttpsInspection(enable.IsChecked==true,excluded.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));httpsStatus.Text="Сохранено. "+certificateState();}catch(Exception ex){enable.IsChecked=backend.ModernStore.Settings.HttpsInspection;httpsStatus.Text=ex.Message;}finally{apply.IsEnabled=true;}};
        install.Click+=(_,_)=>{try{HttpsCertificate.Install(backend.ModernStore.Folder);httpsStatus.Text=certificateState();}catch(Exception ex){httpsStatus.Text=ex.Message;}};
        remove.Click+=async(_,_)=>{try{await backend.ModernSetHttpsInspection(false,backend.ModernStore.Settings.HttpsExcludedDomains);enable.IsChecked=false;HttpsCertificate.Remove(backend.ModernStore.Folder);httpsStatus.Text=certificateState();}catch(Exception ex){httpsStatus.Text=ex.Message;}};
        clear.Click+=(_,_)=>{backend.ModernSession.HttpsInspector?.Policy.Clear();httpsStatus.Text="Временные правила очищены. Новые запросы снова используют обычные маршруты.";};
        var updates=SettingsPage.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Text=="Обновления");SettingsPage.Children.Insert(updates==null?SettingsPage.Children.Count:SettingsPage.Children.IndexOf(updates),panel);
    }
    void RefreshHttpsStatus()
    {
        if(httpsEvents==null||!httpsEvents.IsVisible)return;
        var inspector=backend.ModernSession.HttpsInspector;
        string text=inspector==null?"Перехват не запущен. Включи настройку и подключи VPN.":"Временных правил: "+inspector.Policy.Rules.Count()+"\n"+string.Join('\n',inspector.Events);
        if(httpsEvents.Text!=text)httpsEvents.Text=text;
    }
}
