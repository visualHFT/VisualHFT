using System;
using System.IO;
using System.Security.Cryptography;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Resolves Kalshi API credentials (key id + private RSA key) for the demo
    /// and prod environments without baking any account-specific values into
    /// source. New users supply their own credentials via:
    ///
    ///   KALSHI_DEMO_KEY_ID  /  KALSHI_DEMO_PEM
    ///   KALSHI_PROD_KEY_ID  /  KALSHI_PROD_PEM
    ///
    /// PEM env vars are full paths to a PEM file; if unset, the resolver also
    /// looks for kalshi-demo.pem / kalshi-prod.pem under
    /// %USERPROFILE%\.visualhft\.
    /// </summary>
    internal static class KalshiCredentials
    {
        public const string DemoKeyIdEnv = "KALSHI_DEMO_KEY_ID";
        public const string DemoPemEnv   = "KALSHI_DEMO_PEM";
        public const string ProdKeyIdEnv = "KALSHI_PROD_KEY_ID";
        public const string ProdPemEnv   = "KALSHI_PROD_PEM";

        private const string DemoPemDefaultName = "kalshi-demo.pem";
        private const string ProdPemDefaultName = "kalshi-prod.pem";

        public static (string KeyId, RSA Rsa) LoadDemo() =>
            Load(DemoKeyIdEnv, DemoPemEnv, DemoPemDefaultName, "demo");

        public static (string KeyId, RSA Rsa) LoadProd() =>
            Load(ProdKeyIdEnv, ProdPemEnv, ProdPemDefaultName, "prod");

        public static bool TryLoadProd(out string keyId, out RSA rsa, out string error)
        {
            try
            {
                (keyId, rsa) = LoadProd();
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                keyId = "";
                rsa = null!;
                error = ex.Message;
                return false;
            }
        }

        private static (string KeyId, RSA Rsa) Load(
            string keyIdEnv, string pemEnv, string defaultPemName, string label)
        {
            var keyId = Environment.GetEnvironmentVariable(keyIdEnv);
            if (string.IsNullOrEmpty(keyId))
                throw new InvalidOperationException(
                    $"Kalshi {label} key id not configured. Set the {keyIdEnv} " +
                    "environment variable to your Kalshi access-key id.");

            var pemPath = ResolvePem(pemEnv, defaultPemName, label);
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(File.ReadAllText(pemPath));
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
            return (keyId, rsa);
        }

        private static string ResolvePem(string envVar, string defaultFileName, string label)
        {
            var fromEnv = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".visualhft", defaultFileName);
            if (File.Exists(defaultPath)) return defaultPath;

            throw new FileNotFoundException(
                $"Kalshi {label} PEM not found. Set {envVar} to the full path of your " +
                $"PEM file, or place it at {defaultPath}.");
        }
    }
}
