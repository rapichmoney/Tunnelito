using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Media3D;

namespace RelayDesk.Orbit;

public static class CountryBorders
{
    sealed record Country(string Name,List<Point3D[]> Rings);
    static readonly Lazy<Dictionary<string,Country>> countries=new(Load);
    static readonly ConcurrentDictionary<string,MeshGeometry3D> meshes=new();
    public static string? Name(string code)=>countries.Value.GetValueOrDefault(code)?.Name;
    public static MeshGeometry3D? Mesh(string code)
    {
        if(!countries.Value.TryGetValue(code,out var country))return null;
        return meshes.GetOrAdd(code,_=>Build(country));
    }
    static Dictionary<string,Country> Load()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n=>n.EndsWith("countries.geojson")).Select(n=>Assembly.GetExecutingAssembly().GetManifestResourceStream(n)!).First();
        using var data=JsonDocument.Parse(stream);var result=new Dictionary<string,Country>(StringComparer.OrdinalIgnoreCase);
        foreach(var feature in data.RootElement.GetProperty("features").EnumerateArray())
        {
            string code=feature.GetProperty("properties").GetProperty("code").GetString()!;
            var country=result.GetValueOrDefault(code)??new Country(feature.GetProperty("properties").GetProperty("name").GetString()!,[]);result[code]=country;
            void Polygon(JsonElement polygon)
            {
                foreach(var ring in polygon.EnumerateArray())
                {
                    var points=ring.EnumerateArray().Select(p=>Sphere(p[1].GetDouble(),p[0].GetDouble())).Distinct().ToArray();
                    if(points.Length<3)continue;
                    var dense=new List<Point3D>();
                    for(int i=0;i<points.Length;i++)
                    {
                        var a=(Vector3D)points[i];var b=(Vector3D)points[(i+1)%points.Length];
                        int steps=Math.Max(1,(int)Math.Ceiling(Math.Acos(Math.Clamp(Vector3D.DotProduct(a,b),-1,1))/.015));
                        for(int j=0;j<steps;j++){var v=a+(b-a)*((double)j/steps);v.Normalize();dense.Add((Point3D)v);}
                    }
                    country.Rings.Add(dense.ToArray());
                }
            }
            var geometry=feature.GetProperty("geometry");var coordinates=geometry.GetProperty("coordinates");
            if(geometry.GetProperty("type").GetString()=="Polygon")Polygon(coordinates);else foreach(var polygon in coordinates.EnumerateArray())Polygon(polygon);
        }
        return result;
    }
    static Point3D Sphere(double latitude,double longitude)
    {
        double lat=latitude*Math.PI/180,lon=longitude*Math.PI/180;return new(Math.Cos(lat)*Math.Sin(lon),Math.Sin(lat),Math.Cos(lat)*Math.Cos(lon));
    }
    static MeshGeometry3D Build(Country country)
    {
        var mesh=new MeshGeometry3D();
        foreach(var ring in country.Rings)
        {
            int first=mesh.Positions.Count;
            for(int i=0;i<ring.Length;i++)
            {
                var center=(Vector3D)ring[i];var tangent=ring[(i+1)%ring.Length]-ring[(i+ring.Length-1)%ring.Length];
                var side=Vector3D.CrossProduct(center,tangent);if(side.LengthSquared<1e-18)side=Vector3D.CrossProduct(center,new Vector3D(0,1,0));if(side.LengthSquared<1e-18)side=new Vector3D(1,0,0);side.Normalize();
                mesh.Positions.Add((Point3D)(center*1.006+side*.003));mesh.Positions.Add((Point3D)(center*1.006-side*.003));
            }
            for(int i=0;i<ring.Length;i++)
            {
                int a=first+2*i,b=first+2*((i+1)%ring.Length);foreach(int index in new[]{a,b,a+1,a+1,b,b+1})mesh.TriangleIndices.Add(index);
            }
        }
        mesh.Freeze();return mesh;
    }
}
