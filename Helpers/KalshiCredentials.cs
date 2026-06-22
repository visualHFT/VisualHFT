using System;

namespace VisualHFT.Helpers
{
    /// <summary>
    /// Reads Kalshi API credentials from environment variables so secrets stay
    /// out of source control. Set these in your shell or system environment
    /// before running:
    ///   KALSHI_DEMO_KEY_ID    — demo environment API access key id (UUID)
    ///   KALSHI_DEMO_PEM_PATH  — absolute path to demo RSA private key (.pem)
    ///   KALSHI_PROD_KEY_ID    — prod environment API access key id (UUID)
    ///   KALSHI_PROD_PEM_PATH  — absolute path to prod RSA private key (.pem)
    /// Generate keys at https://kalshi.com (or https://demo.kalshi.co for demo)
    /// → Profile → API Keys.
    /// </summary>
    internal static class KalshiCredentials
    {
        public const string DemoBase = "https://demo-api.kalshi.co";
        public const string ProdBase = "https://api.elections.kalshi.com";

        public static string DemoKeyId   => Require("KALSHI_DEMO_KEY_ID");
        public static string DemoPemPath => Require("KALSHI_DEMO_PEM_PATH");
        public static string ProdKeyId   => Require("KALSHI_PROD_KEY_ID");
        public static string ProdPemPath => Require("KALSHI_PROD_PEM_PATH");

        public static string? TryGetProdKeyId()   => Environment.GetEnvironmentVariable("KALSHI_PROD_KEY_ID");
        public static string? TryGetProdPemPath() => Environment.GetEnvironmentVariable("KALSHI_PROD_PEM_PATH");

        private static string Require(string name) =>
            Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException(
                    $"Environment variable '{name}' is not set. " +
                    "See Helpers/KalshiCredentials.cs for setup instructions.");
    }
}
