using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RelayDesk;
public static class HttpsCertificate
{
    const string Name="CN=Tunnelito Local HTTPS CA";
    static string FilePath(string folder)=>Path.Combine(folder,"https-inspection","authority.dpapi");
    public static X509Certificate2 Load(string folder,bool create=true)
    {
        string file=FilePath(folder);
        if(File.Exists(file))
        {
            var plain=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser);
            try{return X509CertificateLoader.LoadPkcs12(plain,null,X509KeyStorageFlags.EphemeralKeySet|X509KeyStorageFlags.Exportable);}finally{CryptographicOperations.ZeroMemory(plain);}
        }
        if(!create)throw new InvalidOperationException("Сертификат Tunnelito не установлен. Нажми «Установить сертификат». ");
        var subject=new X500DistinguishedName(Name+" "+Guid.NewGuid().ToString("N")[..12],X500DistinguishedNameFlags.ForceUTF8Encoding);
        using var key=RSA.Create(3072);var request=new CertificateRequest(subject,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,true,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(2));
        var pfx=certificate.Export(X509ContentType.Pfx);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try{File.WriteAllBytes(file,ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser));}finally{CryptographicOperations.ZeroMemory(pfx);}
        return Load(folder,false);
    }
    public static string Install(string folder)
    {
        using var certificate=Load(folder);using var root=new X509Store(StoreName.Root,StoreLocation.CurrentUser);root.Open(OpenFlags.ReadWrite);
        if(root.Certificates.Find(X509FindType.FindByThumbprint,certificate.Thumbprint,false).Count==0){using var publicOnly=X509CertificateLoader.LoadCertificate(certificate.RawData);root.Add(publicOnly);}return certificate.Thumbprint;
    }
    public static bool Trusted(string folder)
    {
        if(!File.Exists(FilePath(folder)))return false;
        using var certificate=Load(folder,false);using var root=new X509Store(StoreName.Root,StoreLocation.CurrentUser);root.Open(OpenFlags.ReadOnly);
        return root.Certificates.Find(X509FindType.FindByThumbprint,certificate.Thumbprint,false).Count>0;
    }
    public static void Remove(string folder)
    {
        if(!File.Exists(FilePath(folder)))return;using var certificate=Load(folder,false);using var root=new X509Store(StoreName.Root,StoreLocation.CurrentUser);root.Open(OpenFlags.ReadWrite);
        foreach(var match in root.Certificates.Find(X509FindType.FindByThumbprint,certificate.Thumbprint,false))root.Remove(match);
    }
}
