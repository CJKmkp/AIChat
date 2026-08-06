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
    /// 插件设置页（master-detail 双栏）：左侧提供商列表（添加/删除/切换），
    /// 右侧编辑选中提供商的 名称/协议/地址/API Key/模型，底部为全局设置（系统提示词/温度/MaxTokens）。
    /// 表单直接编辑选中的 <see cref="ProviderConfig"/> 对象，「保存」统一落盘。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public ConfigStore Config { get; set; }
        public Action<string, Ink_Canvas.Plugins.NotificationLevel> Notify { get; set; }
        public Func<Task<List<string>>> ListModelsAsync { get; set; }
        /// <summary>测试连接：入参为当前编辑的 provider，返回 (是否成功, 结果/错误信息)。</summary>
        public Func<ProviderConfig, Task<(bool, string)>> TestConnectionAsync { get; set; }

        private bool _loading;
        private bool _keyShown;

        public SettingsView()
        {
            InitializeComponent();
            if (Config != null) LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            RefreshProviderList();
            // 显式加载表单（RefreshProviderList 里 SelectedItem 变化可能被 _loading guard 跳过）
            LoadForm(ProviderList.SelectedItem as ProviderConfig ?? Config.GetCurrentProvider());
            UpdateDeleteButton();
        }

        // ---------- 左列：提供商列表 ----------

        private void RefreshProviderList(string selectId = null)
        {
            ProviderList.ItemsSource = null;
            var list = Config.GetAllProviders();
            ProviderList.ItemsSource = list;
            ProviderConfig target = null;
            if (selectId != null)
            {
                foreach (var p in list)
                {
                    if (string.Equals(p.Id, selectId, StringComparison.Ordinal)) { target = p; break; }
                }
            }
            if (target == null && ProviderList.SelectedItem is ProviderConfig cur) target = cur;
            if (target == null) target = Config.GetCurrentProvider();
            ProviderList.SelectedItem = target;
            UpdateDeleteButton();
        }

        private void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (ProviderList.SelectedItem is ProviderConfig p)
            {
                Config.SetCurrentProvider(p.Id);
                LoadForm(p);
                UpdateDeleteButton();
            }
        }

        private void UpdateDeleteButton()
        {
            BtnDeleteProvider.IsEnabled = Config.GetAllProviders().Count > 1;
        }

        private void BtnAddProvider_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var preset in ProviderPresets.Presets)
            {
                var mi = new MenuItem { Header = preset.Name, Tag = preset.Key };
                mi.Click += (_, __) => AddProviderFromTemplate(preset.Key);
                menu.Items.Add(mi);
            }
            menu.PlacementTarget = (FrameworkElement)sender;
            menu.IsOpen = true;
        }

        private void AddProviderFromTemplate(string templateKey)
        {
            var provider = Config.AddProvider(templateKey);
            RefreshProviderList(provider.Id);
            LoadForm(provider);
            Notify?.Invoke("已添加提供商", Ink_Canvas.Plugins.NotificationLevel.Success);
        }

        private void BtnDeleteProvider_Click(object sender, RoutedEventArgs e)
        {
            var p = ProviderList.SelectedItem as ProviderConfig ?? Config.GetCurrentProvider();
            if (p == null) return;
            if (Config.GetAllProviders().Count <= 1)
            {
                StatusText.Text = Strings.Get("Provider_CantDeleteLast");
                Notify?.Invoke(Strings.Get("Provider_CantDeleteLast"), Ink_Canvas.Plugins.NotificationLevel.Warning);
                return;
            }
            var msg = string.Format(Strings.Get("Provider_ConfirmDelete"), p.Name);
            var r = MessageBox.Show(msg, Strings.Get("Provider_Delete"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
            Config.RemoveProvider(p.Id);
            RefreshProviderList();
            LoadForm(ProviderList.SelectedItem as ProviderConfig ?? Config.GetCurrentProvider());
            Notify?.Invoke("已删除提供商", Ink_Canvas.Plugins.NotificationLevel.Success);
        }

        // ---------- 右列：编辑表单 ----------

        private void LoadForm(ProviderConfig p)
        {
            if (p == null) return;
            _loading = true;
            try
            {
                NameBox.Text = p.Name;
                SelectComboByTag(TypeCombo, p.IsAnthropic ? "anthropic" : "openai");
                UpdateTypeNote();
                BaseUrlBox.Text = p.BaseUrl;
                ModelBox.Text = p.Model;
                RefreshModelDropdown(p.Models ?? new List<string>(), p.Model);
                // 全局
                SystemPromptBox.Text = Config.Current.SystemPrompt;
                TemperatureBox.Text = Config.Current.Temperature > 0 ? Config.Current.Temperature.ToString() : "";
                MaxTokensBox.Text = Config.Current.MaxTokens > 0 ? Config.Current.MaxTokens.ToString() : "";
                // API Key
                var keyPlain = Config.GetApiKeyPlain();
                ApiKeyBox.Password = string.IsNullOrEmpty(keyPlain) ? "" : new string('*', Math.Min(16, keyPlain.Length));
                ApiKeyPlainBox.Text = keyPlain;
                _keyShown = false;
                ApiKeyPlainBox.Visibility = Visibility.Collapsed;
                ApiKeyBox.Visibility = Visibility.Visible;
                BtnToggleKey.Content = Strings.Get("Settings_ShowKey");
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

        private static string GetSelectedTag(ComboBox combo)
            => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateTypeNote();
            // 同步写回选中 provider 的 Type（对象是引用，天然回写；保存时统一落盘）
            if (ProviderList.SelectedItem is ProviderConfig p)
            {
                p.Type = string.Equals(GetSelectedTag(TypeCombo), "anthropic", StringComparison.OrdinalIgnoreCase)
                    ? "anthropic" : "openai-compatible";
            }
        }

        private void UpdateTypeNote()
        {
            var isAnthropic = string.Equals(GetSelectedTag(TypeCombo), "anthropic", StringComparison.OrdinalIgnoreCase);
            TypeNote.Text = isAnthropic ? Strings.Get("Settings_ProtocolNote") : Strings.Get("Settings_OpenAiNote");
            if (isAnthropic && string.IsNullOrEmpty(ModelBox.Text))
                ModelBox.Text = "claude-haiku-4-5";
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
                // 写入当前 provider 的 Models（不再改内置预设）
                var p = ProviderList.SelectedItem as ProviderConfig ?? Config.GetCurrentProvider();
                p.Models = models;
                RefreshModelDropdown(models, p.Model);
                StatusText.Text = $"已获取 {models.Count} 个模型，从下拉中选择";
                Notify?.Invoke($"已获取 {models.Count} 个模型", Ink_Canvas.Plugins.NotificationLevel.Success);
            }
            catch (Exception ex)
            {
                StatusText.Text = Strings.Get("Info_TestFail", ex.Message);
                Notify?.Invoke(Strings.Get("Info_TestFail", ex.Message), Ink_Canvas.Plugins.NotificationLevel.Error);
            }
        }

        /// <summary>把模型列表填充到 ModelCombo 下拉（可选模型），不覆盖当前 ModelBox 文本。</summary>
        private void RefreshModelDropdown(List<string> models, string currentModel)
        {
            ModelCombo.Items.Clear();
            foreach (var m in models)
            {
                ModelCombo.Items.Add(new ComboBoxItem { Content = m, Tag = m });
            }
            var cur = currentModel?.Trim() ?? "";
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

        /// <summary>
        /// 把右列表单（含 API Key）写回选中的 provider 对象，不落盘。
        /// 返回 false 表示必填缺失（已显示提示）。
        /// </summary>
        public bool SaveToConfig()
        {
            if (Config == null) return false;
            var p = ProviderList.SelectedItem as ProviderConfig ?? Config.GetCurrentProvider();
            if (p == null) return false;
            p.Name = (NameBox.Text ?? "").Trim();
            p.Type = string.Equals(GetSelectedTag(TypeCombo), "anthropic", StringComparison.OrdinalIgnoreCase)
                ? "anthropic" : "openai-compatible";
            p.BaseUrl = (BaseUrlBox.Text ?? "").Trim();
            var modelText = (ModelBox.Text ?? "").Trim();
            // Model 为空时，优先用该 provider 的第一个模型，避免空 model 导致拉取/测试失败
            if (string.IsNullOrEmpty(modelText) && p.Models.Count > 0)
            {
                modelText = p.Models[0];
                ModelBox.Text = modelText;
            }
            p.Model = modelText;
            // API Key
            string newKey = _keyShown ? ApiKeyPlainBox.Text : (ApiKeyBox.Password ?? "");
            if (string.IsNullOrEmpty(newKey) || newKey.All(c => c == '*'))
            {
                if (string.IsNullOrEmpty(p.ApiKeyCipher))
                {
                    StatusText.Text = "请填写 API Key";
                    return false;
                }
            }
            else
            {
                p.ApiKeyCipher = Convert.ToBase64String(SecretStore.ProtectString(newKey));
            }
            // 全局
            Config.Current.SystemPrompt = (SystemPromptBox.Text ?? "").Trim();
            if (double.TryParse(TemperatureBox.Text, out var t) && t >= 0 && t <= 2)
                Config.Current.Temperature = t;
            else Config.Current.Temperature = 0;
            if (int.TryParse(MaxTokensBox.Text, out var mt) && mt > 0 && mt <= 1048576)
                Config.Current.MaxTokens = mt;
            else
                Config.Current.MaxTokens = 0;
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

        private void BtnSave_Click(object sender, RoutedEventArgs e)
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
                        ProviderList.SelectedItem as ProviderConfig ?? Config.GetCurrentProvider());
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
