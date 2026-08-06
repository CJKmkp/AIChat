using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIChat.ChatService;
using AIChat.Views;
using Ink_Canvas.Plugins;

namespace AIChat.Views
{
    /// <summary>
    /// 插件设置页：协议 / 预设 / 接口地址 / API Key / 模型 / 系统提示词 / 测试连接。
    /// 由 AIChatPlugin 在构造时注入 ConfigStore + HostNotifier。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public ConfigStore Config { get; set; }
        public Action<string, Ink_Canvas.Plugins.NotificationLevel> Notify { get; set; }
        public Func<string, ProtocolKind, string, Task<bool>> TestConnectionAsync { get; set; }

        private bool _keyShown;
        private bool _loading;

        public SettingsView()
        {
            InitializeComponent();
            LoadPresets();
            if (Config != null) LoadFromConfig();
        }

        private void LoadPresets()
        {
            PresetCombo.Items.Clear();
            foreach (var p in ProviderPresets.Map)
            {
                var item = new ComboBoxItem { Content = p.Value.DisplayName, Tag = p.Key };
                PresetCombo.Items.Add(item);
            }
        }

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                // 协议
                foreach (ComboBoxItem item in ProtocolCombo.Items)
                {
                    if ((string)item.Tag == (Config.Current.Protocol == ProtocolKind.Anthropic ? "anthropic" : "openai"))
                    {
                        ProtocolCombo.SelectedItem = item; break;
                    }
                }
                UpdateProtocolNote();
                // 预设
                foreach (ComboBoxItem item in PresetCombo.Items)
                {
                    if ((ProviderPreset)item.Tag == Config.Current.Preset)
                    {
                        PresetCombo.SelectedItem = item; break;
                    }
                }
                BaseUrlBox.Text = Config.Current.BaseUrl;
                ModelBox.Text = Config.Current.Model;
                SystemPromptBox.Text = Config.Current.SystemPrompt;
                TemperatureBox.Text = Config.Current.Temperature > 0 ? Config.Current.Temperature.ToString() : "";
                MaxTokensBox.Text = Config.Current.MaxTokens > 0 ? Config.Current.MaxTokens.ToString() : "";
                // API Key 用密文填入 PasswordBox（不显示明文）
                var keyPlain = Config.GetApiKeyPlain();
                if (string.IsNullOrEmpty(keyPlain))
                {
                    ApiKeyBox.Password = "";
                    ApiKeyPlainBox.Text = "";
                }
                else
                {
                    ApiKeyBox.Password = new string('*', Math.Min(16, keyPlain.Length));
                    ApiKeyPlainBox.Text = keyPlain;
                }
            }
            finally { _loading = false; }
        }

        private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateProtocolNote();
        }

        private void UpdateProtocolNote()
        {
            if (ProtocolCombo.SelectedItem is ComboBoxItem item
                && (string)item.Tag == "anthropic")
            {
                ProtocolNote.Text = Strings.Get("Settings_ProtocolNote");
                ModelBox.Text = string.IsNullOrEmpty(ModelBox.Text) || ModelBox.Text.Contains("/")
                    ? "claude-haiku-4-5" : ModelBox.Text;
            }
            else
            {
                ProtocolNote.Text = Strings.Get("Settings_OpenAiNote");
            }
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PresetCombo.SelectedItem == null) return;
            if (PresetCombo.SelectedItem is ComboBoxItem item
                && item.Tag is ProviderPreset preset
                && ProviderPresets.Map.TryGetValue(preset, out var info))
            {
                // 应用预设：填入 BaseUrl/Model；自定义保持不变
                if (preset != ProviderPreset.Custom)
                {
                    BaseUrlBox.Text = info.BaseUrl;
                    ModelBox.Text = info.Model;
                    // 协议跟随预设（Claude 用 Anthropic）
                    foreach (ComboBoxItem pi in ProtocolCombo.Items)
                    {
                        var tag = (string)pi.Tag;
                        var want = info.Protocol == ProtocolKind.Anthropic ? "anthropic" : "openai";
                        if (tag == want) { ProtocolCombo.SelectedItem = pi; break; }
                    }
                }
            }
        }

        private void BtnToggleKey_Click(object sender, RoutedEventArgs e)
        {
            _keyShown = !_keyShown;
            if (_keyShown)
            {
                ApiKeyPlainBox.Visibility = Visibility.Visible;
                ApiKeyBox.Visibility = Visibility.Collapsed;
                BtnToggleKey.Content = Strings.Get("Settings_HideKey");
                ApiKeyPlainBox.Text = Config?.GetApiKeyPlain() ?? "";
            }
            else
            {
                ApiKeyPlainBox.Visibility = Visibility.Collapsed;
                ApiKeyBox.Visibility = Visibility.Visible;
                BtnToggleKey.Content = Strings.Get("Settings_ShowKey");
            }
        }

        public bool SaveToConfig()
        {
            if (Config == null) return false;
            // 协议
            if (ProtocolCombo.SelectedItem is ComboBoxItem pi)
            {
                Config.Current.Protocol = (string)pi.Tag == "anthropic" ? ProtocolKind.Anthropic : ProtocolKind.OpenAiCompatible;
            }
            // 预设
            if (PresetCombo.SelectedItem is ComboBoxItem pri && pri.Tag is ProviderPreset preset)
            {
                Config.Current.Preset = preset;
            }
            // BaseUrl / Model
            Config.Current.BaseUrl = (BaseUrlBox.Text ?? "").Trim();
            Config.Current.Model = (ModelBox.Text ?? "").Trim();
            Config.Current.SystemPrompt = (SystemPromptBox.Text ?? "").Trim();
            // 温度
            if (double.TryParse(TemperatureBox.Text, out var t) && t >= 0 && t <= 2)
                Config.Current.Temperature = t;
            else Config.Current.Temperature = 0;
            // MaxTokens
            if (int.TryParse(MaxTokensBox.Text, out var mt) && mt > 0 && mt <= 128000)
                Config.Current.MaxTokens = mt;
            else Config.Current.MaxTokens = 4096;
            // API Key
            string newKey = _keyShown ? ApiKeyPlainBox.Text : (ApiKeyBox.Password ?? "");
            // 若用户清空了占位符（password box 用 * 占位），保留旧值
            if (string.IsNullOrEmpty(newKey) || newKey.All(c => c == '*'))
            {
                if (Config.GetApiKeyPlain().Length == 0)
                {
                    StatusText.Text = "请填写 API Key";
                    return false;
                }
                // 保留旧 key
            }
            else
            {
                Config.SetApiKeyPlain(newKey);
            }
            return true;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (SaveToConfig())
            {
                try { Config.SaveConfig(); } catch { }
                StatusText.Text = Strings.Get("Settings_Saved");
                Notify?.Invoke(Strings.Get("Settings_Saved"), Ink_Canvas.Plugins.NotificationLevel.Success);
            }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            // 临时保存当前输入到内存（不写盘）
            var ok = SaveToConfig();
            if (!ok) return;
            StatusText.Text = Strings.Get("Chat_Status_Connecting");
            try
            {
                bool pass;
                if (TestConnectionAsync != null)
                {
                    pass = await TestConnectionAsync(
                        Config.GetApiKeyPlain(),
                        Config.Current.Protocol,
                        Config.Current.BaseUrl);
                }
                else
                {
                    pass = false;
                }
                if (pass)
                {
                    StatusText.Text = Strings.Get("Info_TestOk");
                    Notify?.Invoke(Strings.Get("Info_TestOk"), Ink_Canvas.Plugins.NotificationLevel.Success);
                }
                else
                {
                    StatusText.Text = Strings.Get("Info_TestFail", "请检查接口地址与 Key");
                    Notify?.Invoke(Strings.Get("Info_TestFail", "请检查接口地址与 Key"), Ink_Canvas.Plugins.NotificationLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = Strings.Get("Info_TestFail", ex.Message);
                Notify?.Invoke(Strings.Get("Info_TestFail", ex.Message), Ink_Canvas.Plugins.NotificationLevel.Error);
            }
        }
    }

    /// <summary>
    /// 通知级别枚举（与宿主 INotificationService.NotificationLevel 对齐）。
    /// </summary>
    public enum NotificationLevel
    {
        Info, Warning, Error, Success
    }
}