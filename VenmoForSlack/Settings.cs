using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VenmoForSlack.Models;

namespace VenmoForSlack
{
    public static class Settings
    {
        public static SettingsObject SettingsObject { get; private set; }
        static Settings()
        {
            string str = File.ReadAllText("settings.json", Encoding.UTF8);
            SettingsObject = JsonConvert.DeserializeObject<SettingsObject>(str);
        }
    }
}
