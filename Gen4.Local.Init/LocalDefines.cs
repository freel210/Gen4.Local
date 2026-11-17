namespace Gen4.Local.Init;

public static class LocalDefines
{
    public struct Nats
    {
        public const string Endpoint = "nats:nats@127.0.0.1:4222";
    }

    public struct RsaKeys
    {
        public const string PrivateKeyPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\Keys\privateKey.txt";
        public const string PublicKeyPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\Keys\publicKey.txt";
    }

    public struct Lyra3
    {
        public const string IsEnabledKey = "/Configuration/Lyra3Options/IsEnabled";
        public const string IsEnabled = "true";
        public const string ServiceUrlKey = "/Configuration/Lyra3Options/ServiceUrl";
        public const string ServiceUrl = "http://localhost";
    }
}
