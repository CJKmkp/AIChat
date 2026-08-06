using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIChat.ChatService;

namespace AIChat.Views
{
    /// <summary>
    /// 插件设置页：协议 / Provider / 接口地址 / API Key / 模型（含拉取列表）/ 系统提示词 / 测试连接。
    /// 参考 CCSwitch 标准接口：provider 预设 + baseUrl + apiKey + models。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public ConfigStore Config { get; set; }
        public Action<string, Ink_Canvas.Plugins.NotificationLevel> Notify { get; set; }
        public Func<Task<List<string>>> ListModelsAsync { get; set; }
        /// <summary>测试连接：返回 (是否成功, 结果/错误信息)。</summary>
        public Func<string, ProtocolKind, string, Task<(bool, string)>> TestConnectionAsync { get; set; }

        private bool _keyShown;
        private bool _loading;

        public SettingsView()
        {
            InitializeComponent();
            LoadProviders();
            if (Config != null) LoadFromConfig();
        }

        private void LoadProviders()
        {
            ProviderCombo.Items.Clear();
            foreach (var p in ProviderPresets.Presets)
            {
                var item = new ComboBoxItem { Content = p.Name, Tag = p.Key };
                ProviderCombo.Items.Add(item);
            }
        }

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                // 协议
                SelectComboByTag(ProtocolCombo,
                    Config.Current.Protocol == ProtocolKind.Anthropic ? "anthropic" : "openai");
                UpdateProtocolNote();
                // Provider
                SelectComboByTag(ProviderCombo, Config.Current.ProviderKey);
                BaseUrlBox.Text = Config.Current.BaseUrl;
                ModelBox.Text = Config.Current.Model;
                SystemPromptBox.Text = Config.Current.SystemPrompt;
                TemperatureBox.Text = Config.Current.Temperature > 0 ? Config.Current.Temperature.ToString() : "";
                MaxTokensBox.Text = Config.Current.MaxTokens > 0 ? Config.Current.MaxTokens.ToString() : "";
                // API Key
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

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                var itemTag = item.Tag as string;
                if (string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item; return;
                }
            }
        }

        private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateProtocolNote();
        }

        private void UpdateProtocolNote()
        {
            var isAnthropic = ProtocolCombo.SelectedItem is ComboBoxItem item
                && string.Equals(item.Tag as string, "anthropic", StringComparison.OrdinalIgnoreCase);
            ProtocolNote.Text = isAnthropic ? Strings.Get("Settings_ProtocolNote") : Strings.Get("Settings_OpenAiNote");
            if (isAnthropic && string.IsNullOrEmpty(ModelBox.Text))
                ModelBox.Text = "claude-haiku-4-5";
        }

        private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ProviderCombo.SelectedItem == null) return;
            if (ProviderCombo.SelectedItem is ComboBoxItem item
                && item.Tag is string key)
            {
                var preset = ProviderPresets.FindByKey(key);
                if (preset == null) return;
                if (preset.BaseUrl.Length > 0) BaseUrlBox.Text = preset.BaseUrl;
                if (preset.Models.Count > 0 && string.IsNullOrEmpty(ModelBox.Text))
                    ModelBox.Text = preset.Models[0];
                var isAnthropic = string.Equals(preset.Type, "anthropic", StringComparison.OrdinalIgnoreCase);
                SelectComboByTag(ProtocolCombo, isAnthropic ? "anthropic" : "openai");
                UpdateProtocolNote();
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

        private async void BtnFetchModels_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveToConfig()) return;
            StatusText.Text = Strings.Get("Chat_Status_Connecting");
            try
            {
                if (ListModelsAsync == null) { StatusText.Text = "不支持拉取模型列表"; return; }
                var models = await ListModelsAsync();
                if (models == null || models.Count == 0)
                {
                    StatusText.Text = "未获取到模型（该端点可能不支持 /models）";
                    return;
                }
                // 更新 provider 预设的模型列表（不覆盖用户当前选择的模型）
                var preset = ProviderPresets.FindByKey(Config.Current.ProviderKey);
                if (preset != null && Config.Current.ProviderKey != "custom")
                {
                    preset.Models.Clear();
                    preset.Models.AddRange(models);
                }
                RefreshModelDropdown(models);
                StatusText.Text = $"已获取 {models.Count} 个模型，从下拉中选择";
                Notify?.Invoke($"已获取 {models.Count} 个模型", Ink_Canvas.Plugins.NotificationLevel.Success);
            }
            catch (Exception ex)
            {
                StatusText.Text = Strings.Get("Info_TestFail", ex.Message);
                Notify?.Invoke(Strings.Get("Info_TestFail", ex.Message), Ink_Canvas.Plugins.NotificationLevel.Error);
            }
        }

        /// <summary>
        /// 把模型列表填充到 ModelCombo 下拉（可选模型），不覆盖当前 ModelBox 文本。
        /// </summary>
        private void RefreshModelDropdown(List<string> models)
        {
            ModelCombo.Items.Clear();
            foreach (var m in models)
            {
                ModelCombo.Items.Add(new ComboBoxItem { Content = m, Tag = m });
            }
            // 当前模型若在列表中则选中
            var cur = ModelBox.Text?.Trim() ?? "";
            foreach (ComboBoxItem item in ModelCombo.Items)
            {
                if (string.Equals(item.Tag as string, cur, StringComparison.OrdinalIgnoreCase))
                {
                    ModelCombo.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>模型下拉选择：把选中模型填到 ModelBox。</summary>
        private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (ModelCombo.SelectedItem is ComboBoxItem item && item.Tag is string m)
            {
                ModelBox.Text = m;
            }
        }

        public bool SaveToConfig()
        {
            if (Config == null) return false;
            // 协议
            if (ProtocolCombo.SelectedItem is ComboBoxItem pi)
                Config.Current.Protocol = string.Equals(pi.Tag as string, "anthropic", StringComparison.OrdinalIgnoreCase)
                    ? ProtocolKind.Anthropic : ProtocolKind.OpenAiCompatible;
            // Provider
            if (ProviderCombo.SelectedItem is ComboBoxItem pri && pri.Tag is string key)
            {
                Config.Current.ProviderKey = key;
                var preset = ProviderPresets.FindByKey(key);
                if (preset != null && key != "custom")
                {
                    if (preset.BaseUrl.Length > 0) Config.Current.BaseUrl = preset.BaseUrl;
                    Config.Current.Protocol = string.Equals(preset.Type, "anthropic", StringComparison.OrdinalIgnoreCase)
                        ? ProtocolKind.Anthropic : ProtocolKind.OpenAiCompatible;
                }
            }
            // BaseUrl / Model
            Config.Current.BaseUrl = (BaseUrlBox.Text ?? "").Trim();
            var modelText = (ModelBox.Text ?? "").Trim();
            // Model 为空时，优先用 provider 预设的第一个模型，避免空 model 导致拉取/测试失败
            if (string.IsNullOrEmpty(modelText))
            {
                var preset = ProviderPresets.FindByKey(Config.Current.ProviderKey);
                if (preset != null && preset.Models.Count > 0)
                {
                    modelText = preset.Models[0];
                    ModelBox.Text = modelText;
                }
            }
            Config.Current.Model = modelText;
            Config.Current.SystemPrompt = (SystemPromptBox.Text ?? "").Trim();
            // 温度
            if (double.TryParse(TemperatureBox.Text, out var t) && t >= 0 && t <= 2)
                Config.Current.Temperature = t;
            else Config.Current.Temperature = 0;
            // MaxTokens：空或 0 表示不发送（服务端用默认值）
            if (int.TryParse(MaxTokensBox.Text, out var mt) && mt > 0 && mt <= 1048576)
                Config.Current.MaxTokens = mt;
            else
                Config.Current.MaxTokens = 0;
            // API Key
            string newKey = _keyShown ? ApiKeyPlainBox.Text : (ApiKeyBox.Password ?? "");
            if (string.IsNullOrEmpty(newKey) || newKey.All(c => c == '*'))
            {
                if (Config.GetApiKeyPlain().Length == 0)
                {
                    StatusText.Text = "请填写 API Key";
                    return false;
                }
            }
            else
            {
                Config.SetApiKeyPlain(newKey);
            }
            return true;
        }

        private async void BtnSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            var baseUrl = (BaseUrlBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(baseUrl))
            {
                StatusText.Text = "请先填写接口地址";
                return;
            }
            StatusText.Text = "测速中…";
            try
            {
                var results = await EndpointSpeedTest.TestEndpointsAsync(new[] { baseUrl }, 8);
                var r = results.FirstOrDefault();
                if (r == null)
                {
                    StatusText.Text = "测速无结果";
                    return;
                }
                if (!string.IsNullOrEmpty(r.Error))
                {
                    StatusText.Text = $"测速失败：{r.Error}";
                    Notify?.Invoke($"测速失败：{r.Error}", Ink_Canvas.Plugins.NotificationLevel.Warning);
                    return;
                }
                var statusText = r.Status == 200 || r.Status == 401 || r.Status == 403
                    ? "端点可达"
                    : $"端点返回 HTTP {r.Status}";
                StatusText.Text = $"{statusText}，延迟 {r.LatencyMs}ms";
                Notify?.Invoke($"{statusText}，延迟 {r.LatencyMs}ms", Ink_Canvas.Plugins.NotificationLevel.Success);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"测速失败：{ex.Message}";
                Notify?.Invoke($"测速失败：{ex.Message}", Ink_Canvas.Plugins.NotificationLevel.Error);
            }
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
            var ok = SaveToConfig();
            if (!ok) return;
            StatusText.Text = Strings.Get("Chat_Status_Connecting");
            try
            {
                bool pass = false;
                string message = "";
                if (TestConnectionAsync != null)
                {
                    (pass, message) = await TestConnectionAsync(
                        Config.GetApiKeyPlain(),
                        Config.Current.Protocol,
                        Config.Current.BaseUrl);
                }
                if (pass)
                {
                    StatusText.Text = message.Length > 0 ? message : Strings.Get("Info_TestOk");
                    Notify?.Invoke(Strings.Get("Info_TestOk"), Ink_Canvas.Plugins.NotificationLevel.Success);
                }
                else
                {
                    var errText = message.Length > 0 ? message : "请检查接口地址与 Key";
                    StatusText.Text = Strings.Get("Info_TestFail", errText);
                    Notify?.Invoke(Strings.Get("Info_TestFail", errText), Ink_Canvas.Plugins.NotificationLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = Strings.Get("Info_TestFail", ex.Message);
                Notify?.Invoke(Strings.Get("Info_TestFail", ex.Message), Ink_Canvas.Plugins.NotificationLevel.Error);
            }
        }
    }
}