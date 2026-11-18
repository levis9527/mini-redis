using MiniRedis.Core.Storage;

namespace MiniRedis.App.Ui;

internal static class Dialogs
{
    public static bool Confirm(IWin32Window owner, string title, string body, bool danger)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 280),
            Font = Theme.Ui(),
            BackColor = Theme.Panel
        };
        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Text = body,
            BackColor = Theme.Panel,
            ScrollBars = ScrollBars.Vertical
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8)
        };
        var ok = Theme.Button(danger ? "确认" : "确认执行", primary: !danger);
        if (danger)
        {
            ok.ForeColor = Theme.Danger;
            ok.BackColor = Theme.Panel;
        }

        ok.DialogResult = DialogResult.OK;
        var cancel = Theme.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        form.Controls.Add(text);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    public static string? Password(IWin32Window owner, string title)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 120),
            Font = Theme.Ui()
        };
        var box = new TextBox { UseSystemPasswordChar = true, Left = 16, Top = 16, Width = 328 };
        var ok = Theme.Button("连接", primary: true);
        ok.DialogResult = DialogResult.OK;
        ok.Left = 250;
        ok.Top = 56;
        var cancel = Theme.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Left = 180;
        cancel.Top = 56;
        form.Controls.Add(box);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

internal sealed class ConnectionDraft
{
    public required ConnectionProfile Profile { get; init; }
    public string? TypedPassword { get; init; }
    public bool SavePassword { get; init; }
    public bool PasswordTouched { get; init; }
}

internal static class ConnectionEditor
{
    public static ConnectionDraft? Show(IWin32Window owner, ConnectionProfile? existing, bool dpapiAvailable, string dpapiScope)
    {
        var profile = existing ?? new ConnectionProfile();
        using var form = new Form
        {
            Text = existing == null ? "添加连接" : "编辑连接",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 460),
            Font = Theme.Ui()
        };
        var name = Field(form, "名称", profile.Name, 16);
        var host = Field(form, "主机", profile.Host, 64);
        var port = Field(form, "端口", profile.Port.ToString(), 112);
        var db = Field(form, "逻辑库", profile.Database.ToString(), 160);
        var user = Field(form, "ACL 用户名（可空）", profile.Username, 208);
        var password = Field(form, "密码（默认不保存）", "", 256);
        password.UseSystemPasswordChar = true;
        var touched = false;
        password.TextChanged += (_, _) => touched = true;
        var save = new CheckBox
        {
            Text = "保存密码（仅当前 Windows 用户的 DPAPI）",
            Left = 16,
            Top = 304,
            Width = 380,
            Checked = profile.SavePassword,
            Enabled = dpapiAvailable
        };
        var note = new Label
        {
            Text = dpapiAvailable ? dpapiScope : "当前系统没有 DPAPI，不能保存密码。",
            Left = 16,
            Top = 330,
            Width = 388,
            Height = 48,
            ForeColor = Theme.Muted
        };
        var tls = new CheckBox { Text = "TLS", Left = 16, Top = 380, Width = 80, Checked = profile.UseTls };
        var anyCert = new CheckBox { Text = "接受不受信任证书（不安全）", Left = 110, Top = 380, Width = 280, Checked = profile.AllowUntrustedTls };
        var connectTimeout = Field(form, "连接超时毫秒", profile.ConnectTimeoutMs.ToString(), 410);
        connectTimeout.Top = 408;
        connectTimeout.Width = 120;
        var commandTimeout = new TextBox { Left = 250, Top = 408, Width = 120, Text = profile.CommandTimeoutMs.ToString() };
        form.Controls.Add(commandTimeout);
        var ok = Theme.Button("保存", primary: true);
        ok.Left = 320;
        ok.Top = 16;
        ok.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        form.AcceptButton = ok;
        form.CancelButton = new Button { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(port.Text, out var portNumber) || !int.TryParse(db.Text, out var dbNumber)
                || !int.TryParse(connectTimeout.Text, out var connectMs) || !int.TryParse(commandTimeout.Text, out var commandMs))
            {
                MessageBox.Show(form, "端口、逻辑库和超时需要是整数。");
                return;
            }

            var next = new ConnectionProfile
            {
                Id = profile.Id,
                Name = name.Text.Trim(),
                Host = host.Text.Trim(),
                Port = portNumber,
                Database = dbNumber,
                Username = user.Text.Trim(),
                UseTls = tls.Checked,
                AllowUntrustedTls = anyCert.Checked,
                ConnectTimeoutMs = connectMs,
                CommandTimeoutMs = commandMs,
                SavePassword = profile.SavePassword,
                ProtectedPasswordBase64 = profile.ProtectedPasswordBase64
            };
            try
            {
                ConnectionStore.Validate(next);
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, ex.Message);
                return;
            }

            form.Tag = new ConnectionDraft
            {
                Profile = next,
                TypedPassword = password.Text,
                SavePassword = save.Checked,
                PasswordTouched = touched || save.Checked != profile.SavePassword
            };
            form.DialogResult = DialogResult.OK;
        };
        return form.ShowDialog(owner) == DialogResult.OK ? form.Tag as ConnectionDraft : null;
    }

    private static TextBox Field(Form form, string label, string value, int top)
    {
        form.Controls.Add(new Label { Text = label, Left = 16, Top = top, Width = 180, ForeColor = Theme.Muted });
        var box = new TextBox { Left = 200, Top = top - 2, Width = 190, Text = value };
        form.Controls.Add(box);
        return box;
    }
}

internal sealed class NewKeyDraft
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Field { get; init; }
    public required string Value { get; init; }
    public required string Score { get; init; }
    public required int TtlSeconds { get; init; }
}

internal static class NewKeyEditor
{
    public static NewKeyDraft? Show(IWin32Window owner)
    {
        using var form = new Form
        {
            Text = "新建键",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 280),
            Font = Theme.Ui()
        };
        var name = Box(form, "键名", 16);
        var type = new ComboBox { Left = 140, Top = 48, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
        type.Items.AddRange(["string", "hash", "list", "set", "zset"]);
        type.SelectedIndex = 0;
        form.Controls.Add(new Label { Text = "类型", Left = 16, Top = 50, Width = 110 });
        form.Controls.Add(type);
        var field = Box(form, "字段 / 成员", 80);
        var value = Box(form, "值", 112);
        var score = Box(form, "分数（ZSet）", 144);
        score.Text = "0";
        var ttl = Box(form, "TTL 秒，-1 为不过期", 176);
        ttl.Text = "-1";
        var ok = Theme.Button("创建", primary: true);
        ok.Left = 300;
        ok.Top = 220;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                MessageBox.Show(form, "请填写键名。");
                return;
            }

            if (!int.TryParse(ttl.Text, out var ttlSeconds) || ttlSeconds < -1)
            {
                MessageBox.Show(form, "TTL 无效。0 会在 Redis 里删除键，这里不接受 0。");
                return;
            }

            if (ttlSeconds == 0)
            {
                MessageBox.Show(form, "TTL 0 会删除键。请用删除，或填写正数秒，或 -1 表示不过期。");
                return;
            }

            form.Tag = new NewKeyDraft
            {
                Name = name.Text,
                Type = type.Text,
                Field = field.Text,
                Value = value.Text,
                Score = score.Text,
                TtlSeconds = ttlSeconds
            };
            form.DialogResult = DialogResult.OK;
        };
        form.CancelButton = new Button { DialogResult = DialogResult.Cancel };
        return form.ShowDialog(owner) == DialogResult.OK ? form.Tag as NewKeyDraft : null;
    }

    private static TextBox Box(Form form, string label, int top)
    {
        form.Controls.Add(new Label { Text = label, Left = 16, Top = top, Width = 120, ForeColor = Theme.Muted });
        var box = new TextBox { Left = 140, Top = top - 2, Width = 250 };
        form.Controls.Add(box);
        return box;
    }
}
