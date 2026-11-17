using System.IO;
namespace RelayDesk;
public static class BrandIdentity
{
    public const string Name="Tunnelito";
    public const string Repository="rapichmoney/Tunnelito";
    public static string DataFolder=>Environment.GetEnvironmentVariable("RELAYDESK_DATA_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelayDesk");
    public static string Executable(string folder)=>Path.Combine(folder,File.Exists(Path.Combine(folder,"Tunnelito.exe"))?"Tunnelito.exe":"RelayDesk.exe");
    public static bool ClaimIntroduction(Store store,bool existing)
    {
        if(store.Settings.BrandingVersion>=1)return false;
        store.Settings.BrandingVersion=1;store.Save();return existing;
    }
}
