using System.ComponentModel;
using System.Globalization;

namespace ArcGisProAppYolo.Infrastructure
{
    internal sealed class LocalizationService : INotifyPropertyChanged
    {
        private static readonly LocalizationService _instance = new LocalizationService();
        public static LocalizationService Instance => _instance;

        private readonly Resources.Strings _strings = new Resources.Strings();

        public Resources.Strings Strings => _strings;

        public string this[string key]
        {
            get
            {
                var value = Resources.Strings.ResourceManager.GetString(key, Resources.Strings.Culture);
                return value ?? $"[{key}]";
            }
        }

        public static string GetString(string key)
        {
            return Instance[key];
        }

        public static string Format(string key, params object[] args)
        {
            var template = Instance[key];
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
            }
        }

        public CultureInfo CurrentCulture
        {
            get => Resources.Strings.Culture;
            set
            {
                if (Equals(Resources.Strings.Culture, value))
                    return;
                Resources.Strings.Culture = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
