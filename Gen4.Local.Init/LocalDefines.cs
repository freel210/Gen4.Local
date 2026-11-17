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
        public const string AuthApiAbsoluteUrlKey = "/Configuration/Lyra3Options/AuthApiAbsoluteUrl";
        public const string AuthApiAbsoluteUrl = "http://localhost:3304";
    }

    public struct Jeconbol3
    {
        public const string ExternalHisType = "jeconbol3";
        public const string IsEnabledKey = "/Configuration/Jeconbol3Options/IsEnabled";
        public const string IsEnabled = "true";
        public const string ServiceUrlKey = "/Configuration/Jeconbol3Options/ServiceUrl";
        public const string ServiceUrl = "http://127.0.0.1:9097";
        public const string MinutesUpdateIntervalKey = "/Configuration/Jeconbol3Options/MinutesUpdateInterval";
        public const string MinutesUpdateInterval = "1";
        public const string ExportMinutesIntervalKey = "/Configuration/Jeconbol3Options/ExportMinutesInterval";
        public const string ExportMinutesInterval = "1";
        public const string OrganizationName = "Организация";
        public const string OperatingRoomId = "11111111-1111-1111-1111-111111111111";
        public const string OperatingRoomName = "Операционная Jeconbol 1";
        public const string OperatingRoomExternalHisId = "00000000-1ada-4f78-a560-ceaa55a10170";
    }
}
