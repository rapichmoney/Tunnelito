using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RelayDesk;

public sealed class DomainBatchWindow:ThemedWindow
{
    public sealed class Row
    {
        public bool Selected{get;set;}=true;
        public required DomainEntry Entry{get;init;}
        public string Label=>Entry.Value+" · "+Entry.Route;
    }
    public List<DomainEntry> Chosen{get;}=[];
    public bool First{get;private set;}
    public DomainBatchWindow(string text,IEnumerable<RouteRule> existing)
    {
        Title="Добавить домены списком";Width=820;Height=700;MinWidth=620;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(24)};Content=root;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(new TextBlock{Text="Домены из буфера",FontSize=24,Margin=new Thickness(0,0,0,12)});
        top.Children.Add(new TextBlock{Text="По одному в строке, через пробел, запятую или точку с запятой. Можно вставить URL, *.example.com или экспорт Tunnelito.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        var input=new TextBox{Text=text,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=140};top.Children.Add(input);
        var options=new WrapPanel{Margin=new Thickness(0,12,0,12)};top.Children.Add(options);
        var kind=new ComboBox{ItemsSource=new[]{"Домен и поддомены","Точный домен","Слово в домене"},SelectedIndex=0,Width=210,Margin=new Thickness(0,0,10,0)};
        var route=new ComboBox{ItemsSource=RouteRules.Routes,SelectedIndex=0,Width=180,Margin=new Thickness(0,0,10,0)};options.Children.Add(kind);options.Children.Add(route);
        var preview=new Button{Content="Проверить список"};options.Children.Add(preview);
        top.Children.Add(new TextBlock{Text="Выбор выше применяется к простому списку. В экспорте Tunnelito тип и маршрут каждой записи сохраняются.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};top.Children.Add(status);
        var bottom=new StackPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var first=new CheckBox{Content="Поместить новые правила перед существующими",Margin=new Thickness(0,0,0,8)};bottom.Children.Add(first);
        bottom.Children.Add(new TextBlock{Text="Совпадающие правила с другим маршрутом будут обновлены после подтверждения. Если новые правила добавлены в конец, более раннее правило приложения или домена может сработать первым.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};bottom.Children.Add(actions);
        var cancel=new Button{Content="Отмена",IsCancel=true,Margin=new Thickness(0,0,8,0)};actions.Children.Add(cancel);
        var add=new Button{Content="Добавить выбранные",IsEnabled=false};actions.Children.Add(add);
        var rows=new ListBox{BorderThickness=new Thickness(1),SelectionMode=SelectionMode.Multiple};ScrollViewer.SetCanContentScroll(rows,true);VirtualizingPanel.SetIsVirtualizing(rows,true);VirtualizingPanel.SetVirtualizationMode(rows,VirtualizationMode.Recycling);root.Children.Add(rows);
        var template=new DataTemplate(typeof(Row));var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(Row.Selected)){Mode=BindingMode.TwoWay});check.SetBinding(ContentControl.ContentProperty,new Binding(nameof(Row.Label)));template.VisualTree=check;rows.ItemTemplate=template;
        var known=existing.Where(r=>r.Kind!="default").ToArray();List<Row> candidates=[];
        void Read()
        {
            try
            {
                var batch=DomainRuleTransfer.Parse(input.Text,new[]{"domain_suffix","domain","domain_keyword"}[kind.SelectedIndex],route.SelectedItem?.ToString()??"Через VPN");
                int exact=0,overlap=0,updates=0;candidates=[];
                foreach(var entry in batch.Entries)
                {
                    var candidate=new RouteRule{Kind=entry.Kind,Value=entry.Value,Route=entry.Route};
                    var existing=known.FirstOrDefault(r=>RuleMatching.SameTarget(r,candidate));
                    if(existing!=null){if(existing.Route==entry.Route){exact++;continue;}updates++;}
                    if(RuleMatching.Overlaps(candidate,known).Count>0)overlap++;
                    candidates.Add(new(){Entry=entry});
                }
                rows.ItemsSource=candidates;add.IsEnabled=candidates.Count>0;
                status.Text=$"Новых: {candidates.Count-updates}. Обновлений маршрута: {updates}. Повторов: {batch.Duplicates}. Уже есть: {exact}. Пересечений: {overlap}. Ошибок: {batch.Errors.Count}."+(batch.Errors.Count>0?"\n"+string.Join("\n",batch.Errors.Take(3)):"");
                if(candidates.Count==0&&exact>0)status.Text+="\nВсе записи уже добавлены с теми же маршрутами. Импорт не требуется.";
            }
            catch(Exception ex){candidates=[];rows.ItemsSource=null;add.IsEnabled=false;status.Text=ex.Message;}
        }
        input.TextChanged+=(_,_)=>{add.IsEnabled=false;status.Text="Нажми «Проверить список» для предпросмотра.";};
        kind.SelectionChanged+=(_,_)=>Read();route.SelectionChanged+=(_,_)=>Read();preview.Click+=(_,_)=>Read();
        add.Click+=(_,_)=>{Chosen.Clear();Chosen.AddRange(candidates.Where(r=>r.Selected).Select(r=>r.Entry));if(Chosen.Count==0){status.Text="Отметь домены для добавления.";return;}First=first.IsChecked==true;DialogResult=true;};
        Read();
    }
}
