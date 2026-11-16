namespace Phonestick
{
    /// <summary>Names in one place, so a rename is a quick search and replace.</summary>
    internal static class AppInfo
    {
        public const string Name = "Phonestick";
        /// <summary>Android applicationId of the phone app.</summary>
        public const string PhonePackage = "io.github.mayerwin.phonestick";
        /// <summary>versionCode of the phone app built into this exe. Keep equal to android/app/build.gradle.</summary>
        public const int PhoneAppVersion = 11;
        /// <summary>Folder on the phone (adb shell side) holding the file server.</summary>
        public const string PhoneDir = "/data/local/tmp/phonestick";
        /// <summary>
        /// Version of the phone's file server (phone-server/, embedded in this exe). Raise it with
        /// any change there: the PC copies the server to the phone again when it differs.
        /// </summary>
        public const string PhoneServerVersion = "1";
        /// <summary>Private adb server port, so this app never disturbs other adb users and can
        /// shut its own server down (closing every connection to the phone) when done.</summary>
        public const int AdbPort = 5038;
        public const string RepoUrl = "https://github.com/mayerwin/Phonestick";
    }
}
