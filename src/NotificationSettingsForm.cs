using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TaskbarTelemetry
{
    internal enum NotificationSettingsAction
    {
        None,
        Enabled,
        Disabled
    }

    internal sealed class NotificationSettingsForm : Form
    {
        private readonly string privacyPolicyPath;
        private readonly string settingsPath;
        private readonly Action disableRuntime;
        private readonly TextBox sendKeyTextBox;
        private readonly CheckBox consentCheckBox;

        internal NotificationSettingsForm(string privacyPolicyPath, string settingsPath, Action disableRuntime)
        {
            this.privacyPolicyPath = privacyPolicyPath;
            this.settingsPath = settingsPath;
            this.disableRuntime = disableRuntime;
            Action = NotificationSettingsAction.None;
            CleanupComplete = true;
            CleanupError = string.Empty;

            SuspendLayout();
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "额度通知设置（Server酱³ / 微信）";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(620, 440);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.Dock = DockStyle.None;
            layout.AutoSize = true;
            layout.MinimumSize = new Size(620, 0);
            layout.MaximumSize = new Size(620, 0);
            layout.Padding = new Padding(18);
            Controls.Add(layout);

            AddTextRow(layout,
                "启用后，应用会把 Codex 额度百分比、提醒档位、窗口时长和重置时间发送给 Server酱。\r\n" +
                "Server酱³（sc3.ft07.com）使用独立 App 接收；Turbo 使用微信等已配置通道接收。",
                SystemColors.ControlText);
            AddTextRow(layout,
                "不会发送会话正文或 Codex 访问令牌。SendKey 仅通过 HTTPS 用于接口鉴权，在本机加密保存，不写入日志。",
                SystemColors.ControlText);

            LinkLabel privacyLink = new LinkLabel();
            privacyLink.AutoSize = true;
            privacyLink.Text = "查看完整隐私政策";
            privacyLink.Margin = new Padding(0, 0, 0, 12);
            privacyLink.LinkClicked += PrivacyLinkClicked;
            layout.Controls.Add(privacyLink);

            LinkLabel keyLink = new LinkLabel();
            keyLink.AutoSize = true;
            keyLink.Text = "打开 Server酱³ 网站获取 SendKey";
            keyLink.Margin = new Padding(0, 0, 0, 12);
            keyLink.LinkClicked += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://sc3.ft07.com/sendkey") { UseShellExecute = true });
                }
                catch
                {
                    MessageBox.Show(this, "无法打开浏览器，请访问 https://sc3.ft07.com/sendkey。", Text);
                }
            };
            layout.Controls.Add(keyLink);

            AddTextRow(layout, "SendKey（Server酱³：sctp数字t密钥；Turbo：SCT 开头）", SystemColors.ControlText);
            sendKeyTextBox = new TextBox();
            sendKeyTextBox.Dock = DockStyle.Fill;
            sendKeyTextBox.MaxLength = 512;
            sendKeyTextBox.UseSystemPasswordChar = true;
            sendKeyTextBox.Margin = new Padding(0, 0, 0, 10);
            layout.Controls.Add(sendKeyTextBox);

            Label channelLabel = AddTextRow(layout, string.Empty, SystemColors.GrayText);
            EventHandler updateChannel = delegate
            {
                string entered = sendKeyTextBox.Text.Trim();
                string effective = entered.Length == 0 ? ReadStoredKey() : entered;
                if (ServerChanEndpoint.IsValid(effective))
                    channelLabel.Text = (entered.Length == 0 ? "已保存：" : "将使用：") +
                        ServerChanEndpoint.GetChannel(effective) +
                        (entered.Length == 0 ? "。更换时粘贴新密钥，否则留空。" : "。保存后使用对应接口发送。");
                else
                    channelLabel.Text = entered.Length == 0
                        ? "请粘贴网站复制的完整 SendKey；密钥只在本机加密保存。"
                        : "密钥格式不正确，请从网站重新复制完整 SendKey。";
            };
            sendKeyTextBox.TextChanged += updateChannel;
            updateChannel(this, EventArgs.Empty);

            consentCheckBox = new CheckBox();
            consentCheckBox.AutoSize = true;
            consentCheckBox.Dock = DockStyle.Fill;
            consentCheckBox.Margin = new Padding(0, 0, 0, 12);
            consentCheckBox.Text = "我已阅读上述说明，同意通过所填密钥对应的 Server酱服务发送额度通知。";
            consentCheckBox.Checked = false;
            layout.Controls.Add(consentCheckBox);

            AddTextRow(layout, "保存后重新打开程序生效；停用会立即停止推送。", SystemColors.GrayText);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            layout.Controls.Add(buttons);

            Button cancelButton = new Button();
            cancelButton.AutoSize = true;
            cancelButton.Padding = new Padding(8, 4, 8, 4);
            cancelButton.Text = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancelButton);

            Button disableButton = new Button();
            disableButton.AutoSize = true;
            disableButton.Padding = new Padding(8, 4, 8, 4);
            disableButton.Text = "停用并删除密钥";
            disableButton.Click += DisableClicked;
            buttons.Controls.Add(disableButton);

            Button enableButton = new Button();
            enableButton.AutoSize = true;
            enableButton.Padding = new Padding(8, 4, 8, 4);
            enableButton.Text = "保存并启用";
            enableButton.Click += EnableClicked;
            buttons.Controls.Add(enableButton);

            AcceptButton = enableButton;
            CancelButton = cancelButton;
            ResumeLayout(true);
        }

        private static Label AddTextRow(TableLayoutPanel layout, string text, Color color)
        {
            Label label = new Label();
            label.AutoSize = true;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 0, 0, 12);
            label.Text = text;
            label.ForeColor = color;
            layout.Controls.Add(label);
            return label;
        }
        internal NotificationSettingsAction Action { get; private set; }
        internal bool CleanupComplete { get; private set; }
        internal string CleanupError { get; private set; }

        private void EnableClicked(object sender, EventArgs e)
        {
            if (!consentCheckBox.Checked)
            {
                MessageBox.Show(this, "请先勾选明确同意项。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string enteredKey = sendKeyTextBox.Text == null ? string.Empty : sendKeyTextBox.Text.Trim();
            string effectiveKey = enteredKey.Length > 0 ? enteredKey : ReadStoredKey();
            if (!IsValidSendKey(effectiveKey))
            {
                MessageBox.Show(this, "请输入有效的 SendKey：Server酱³ 为 sctp数字t密钥，Turbo 为 SCT 开头。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                SendKeyStore.Save(effectiveKey);
                IniSettingsWriter.SetNotificationEnabled(settingsPath, true);
                NotificationConsent.Grant();
                Action = NotificationSettingsAction.Enabled;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法保存通知设置：\r\n" + exception.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DisableClicked(object sender, EventArgs e)
        {
            DialogResult confirmation = MessageBox.Show(
                this,
                "这会立即停用后续通知推送，并删除本机保存的 SendKey 与额度提醒状态。是否继续？",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirmation != DialogResult.Yes)
                return;

            Action = NotificationSettingsAction.Disabled;
            List<string> cleanupErrors = new List<string>();
            TryCleanup("停止当前发送线程", delegate
            {
                if (disableRuntime != null)
                    disableRuntime();
            }, cleanupErrors);
            TryCleanup("关闭下次启动的通知", delegate
            {
                IniSettingsWriter.SetNotificationEnabled(settingsPath, false);
            }, cleanupErrors);
            TryCleanup("撤销通知同意", NotificationConsent.Revoke, cleanupErrors);
            TryCleanup("删除 SendKey", SendKeyStore.Delete, cleanupErrors);
            TryCleanup("删除额度提醒状态", NotificationConsent.DeleteQuotaState, cleanupErrors);

            CleanupComplete = cleanupErrors.Count == 0;
            CleanupError = string.Join("\r\n", cleanupErrors.ToArray());
            DialogResult = DialogResult.OK;
            Close();
        }

        private string ReadStoredKey()
        {
            return SendKeyStore.Read();
        }

        private static void TryCleanup(string name, Action action, IList<string> errors)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                errors.Add(name + "失败：" + exception.Message);
            }
        }

        private static bool IsValidSendKey(string value)
        {
            return ServerChanEndpoint.IsValid(value);
        }

        private void PrivacyLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(privacyPolicyPath) || !File.Exists(privacyPolicyPath))
                    throw new FileNotFoundException("找不到随应用提供的隐私政策。", privacyPolicyPath);
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = privacyPolicyPath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法打开隐私政策：\r\n" + exception.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
