using System.IO;
using System.Reflection;
using System.Runtime.Loader;
namespace OxyPlotBenchmark
{
    public class OxyPlotLoadContext : AssemblyLoadContext
    {
        private Dictionary<string, Assembly> _loadedAssemblies = new();
        private string _basePath;

        public OxyPlotLoadContext(string basePath) : base(isCollectible: true)
        {
            _basePath = basePath;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            string assemblyPath = Path.Combine(_basePath, $"{assemblyName.Name}.dll");
            if (File.Exists(assemblyPath))
            {
                if (_loadedAssemblies.TryGetValue(assemblyPath, out var loadedAssembly))
                    return loadedAssembly;

                var assembly = LoadFromAssemblyPath(assemblyPath);
                _loadedAssemblies[assemblyPath] = assembly;
                return assembly;
            }
            return null;
        }
    }
}