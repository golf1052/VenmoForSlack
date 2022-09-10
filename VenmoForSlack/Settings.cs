using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using VenmoForSlack.Models;

namespace VenmoForSlack
{
    public class Settings
    {
        public SettingsObject SettingsObject { get; private set; }

        public Settings() : this("settings.json")
        {
        }

        public Settings(string settingsPath)
        {
            string str = File.ReadAllText(settingsPath, Encoding.UTF8);
            var settingsObject = JsonConvert.DeserializeObject<SettingsObject>(str);
            if (settingsObject == null)
            {
                throw new Exception($"{settingsPath} is null");
            }
            SettingsObject = settingsObject;
        }
    }
}
