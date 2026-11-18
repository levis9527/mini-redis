using System.Globalization;
using System.Text;
using MiniRedis.Core.Commands;
using MiniRedis.Core.Keys;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Runtime;
using MiniRedis.Core.Scanning;
using MiniRedis.Core.Storage;
using MiniRedis.RedisTransport;

namespace MiniRedis.App.Ui;

internal sealed class MainForm : Form
{
    private readonly AppStorage _storage;
    private readonly ISecretProtector _protector;
    private readonly ConnectionStore _connections;
    private readonly JsonlAuditLog _audit;
    private readonly CommandRunner _runner = new();
    private readonly CommandHistory _history = new();
    private readonly ScanBroker _broker = new();
    private readonly Dictionary<string, string> _passwords = [];
    private readonly List<ConnectionProfile> _profiles = [];
    private readonly List<RedisArg> _keys = [];
    private readonly List<string> _keyTypes = [];
    private readonly ToolTip _tips = new() { AutoPopDelay = 7000, InitialDelay = 400, ReshowDelay = 200 };

    private StackExchangeRedisClient? _client;
    private StackExchangeRedisClient? _terminalClient;
    private TrackedSession? _terminalSession;
    private IRedisSession? _session;
    private StringBuffer _stringBuffer = new();
    private int _appliedDatabase = -1;
    private long _lastPttl = -1;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scanCts;
    private ScanQuery? _query;
    private int _auditSkip;
    private bool _readOnly = true;
    private bool _exactMode;
    private bool _busy;
    private RedisArg? _selected;
    private string _keyHeadline = "";
    private CollectionPage? _page;
    private readonly List<MemberRow> _members = [];

    private ListBox _connList = null!;
    private Label _where = null!;
    private Button _readButton = null!;
    private Button _writeButton = null!;
    private TextBox _pattern = null!;
    private ComboBox _typeFilter = null!;
    private NumericUpDown _count = null!;
    private NumericUpDown _database = null!;
    private Button _start = null!;
    private Button _more = null!;
    private Button _loadAll = null!;
    private Button _stop = null!;
    private Button _create = null!;
    private TextBox _exact = null!;
    private Button _exactRun = null!;
    private Panel _scanRow = null!;
    private Panel _exactRow = null!;
    private ListView _keyList = null!;
    private Label _status = null!;
    private Label _detailMeta = null!;
    private TextBox _valueBox = null!;
    private DataGridView _grid = null!;
    private TextBox _fieldBox = null!;
    private TextBox _valueEdit = null!;
    private TextBox _ttlBox = null!;
    private Button _saveString = null!;
    private Button _moreString = null!;
    private Button _saveMember = null!;
    private Button _addMember = null!;
    private Button _deleteMember = null!;
    private Button _moreMembers = null!;
    private Button _deleteKey = null!;
    private Button _applyTtl = null!;
    private TextBox _terminal = null!;
    private TextBox _terminalInput = null!;
    private ListView _auditList = null!;
    private Label _auditNote = null!;
    private TableLayoutPanel _root = null!;
    private int _terminalRow = 2;
    private int _auditRow = 3;

    public MainForm(AppStorage storage, ISecretProtector protector)
    {
        _storage = storage;
        _protector = protector;
        _connections = new ConnectionStore(storage.ConnectionFile);
        _audit = new JsonlAuditLog(storage.AuditFile);
        _profiles.AddRange(_connections.Load());
        Text = "Mini Redis";
        Font = Theme.Ui();
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Build();
        ReloadConnections();
        UpdateMode();
        SetStatus("尚未连接。连接成功后不会自动扫描。");
        FormClosed += (_, _) => Shutdown();
    }

    private void Build()
    {
        _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(_root);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 8) };
        var title = new Label { Text = "Mini Redis", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Left = 0, Top = 6 };
        _readButton = Theme.Button("只读");
        _writeButton = Theme.Button("可写");
        _readButton.Left = 110;
        _writeButton.Left = 180;
        _readButton.Top = _writeButton.Top = 4;
        _where = new Label { AutoSize = false, Left = 250, Top = 8, Height = 24, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, ForeColor = Theme.Muted };
        var help = Theme.Button("使用说明");
        help.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        help.Top = 4;
        header.Resize += (_, _) => help.Left = header.Width - help.Width - 8;
        header.Resize += (_, _) => _where.Width = Math.Max(80, header.Width - 420);
        _readButton.Click += (_, _) => { _readOnly = true; UpdateMode(); };
        _writeButton.Click += (_, _) => { _readOnly = false; UpdateMode(); };
        help.Click += (_, _) => ShowHelp();
        header.Controls.AddRange([title, _readButton, _writeButton, _where, help]);
        _root.Controls.Add(header, 0, 0);
        Tip(_readButton, "默认只读。重启后仍是只读。");
        Tip(_writeButton, "可写后才能新建、修改、改 TTL 或删除。");
        Tip(help, "打开完整使用说明。");

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
        var host = new Panel { Location = Point.Empty, BackColor = Theme.Bg };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Panel };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(table);
        scroll.Controls.Add(host);
        scroll.Resize += (_, _) =>
        {
            var width = Math.Max(1160, scroll.ClientSize.Width);
            var height = Math.Max(480, scroll.ClientSize.Height);
            host.Size = new Size(width, height);
            scroll.AutoScrollMinSize = new Size(1160, 0);
        };
        table.Controls.Add(BuildConnections(), 0, 0);
        table.Controls.Add(BuildKeys(), 1, 0);
        table.Controls.Add(BuildDetail(), 2, 0);
        _root.Controls.Add(scroll, 0, 1);

        _root.Controls.Add(BuildTerminal(), 0, _terminalRow);
        _root.Controls.Add(BuildAudit(), 0, _auditRow);
        var toggles = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 4, 8, 4), BackColor = Theme.Bg };
        var terminalToggle = Theme.Button("命令面板");
        var auditToggle = Theme.Button("操作记录");
        terminalToggle.Click += (_, _) => ToggleRow(_terminalRow, 176);
        auditToggle.Click += (_, _) => { ToggleRow(_auditRow, 168); ReloadAudit(); };
        toggles.Controls.Add(terminalToggle);
        toggles.Controls.Add(auditToggle);
        _root.Controls.Add(toggles, 0, 4);
        Tip(terminalToggle, "展开 Redis 命令面板，不是操作系统 shell。");
        Tip(auditToggle, "展开本地操作记录。");
    }

    private Control BuildConnections()
    {
        var panel = Column("连接");
        _connList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
        _connList.SelectedIndexChanged += (_, _) => OnConnectionSelected();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        var add = Theme.Button("添加");
        var edit = Theme.Button("编辑");
        var remove = Theme.Button("删除");
        var connect = Theme.Button("连接", primary: true);
        var disconnect = Theme.Button("断开");
        add.Click += (_, _) => EditConnection(null);
        edit.Click += (_, _) => { if (SelectedProfile() is { } profile) EditConnection(profile); };
        remove.Click += (_, _) => DeleteConnection();
        connect.Click += async (_, _) => await Guard(ConnectAsync);
        disconnect.Click += (_, _) => Disconnect();
        buttons.Controls.AddRange([add, edit, remove, connect, disconnect]);
        panel.Controls.Add(_connList);
        panel.Controls.Add(buttons);
        Tip(connect, "连接当前项。不会自动 SCAN。");
        return panel;
    }

    private Control BuildKeys()
    {
        var panel = Column("键");
        var modes = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 6, 8, 0) };
        var scanMode = Theme.Button("SCAN 扫描");
        var exactMode = Theme.Button("精确查询");
        scanMode.Click += (_, _) => SetQueryMode(false);
        exactMode.Click += (_, _) => SetQueryMode(true);
        modes.Controls.Add(scanMode);
        modes.Controls.Add(exactMode);
        _database = new NumericUpDown { Minimum = 0, Maximum = 1023, Width = 64 };
        _database.ValueChanged += (_, _) => ApplyDatabase((int)_database.Value);
        modes.Controls.Add(new Label { Text = "DB", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        modes.Controls.Add(_database);

        _scanRow = new Panel { Dock = DockStyle.Top, Height = 72 };
        _pattern = new TextBox { Left = 8, Top = 4, Width = 180, Text = "*" };
        _typeFilter = new ComboBox { Left = 194, Top = 4, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        _typeFilter.Items.AddRange(["全部类型", "string", "hash", "list", "set", "zset"]);
        _typeFilter.SelectedIndex = 0;
        _count = new NumericUpDown { Left = 290, Top = 4, Width = 64, Minimum = 1, Maximum = 10000, Value = 10 };
        _start = Theme.Button("开始查询", primary: true);
        _more = Theme.Button("加载更多");
        _loadAll = Theme.Button("加载全部");
        _stop = Theme.Button("停止");
        _create = Theme.Button("新建键");
        var row = new FlowLayoutPanel { Left = 8, Top = 32, Height = 36, Width = 640, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.AddRange([_start, _more, _loadAll, _stop, _create]);
        _scanRow.Controls.AddRange([_pattern, _typeFilter, _count, row]);
        _start.Click += async (_, _) => await Guard(StartScanAsync);
        _more.Click += async (_, _) => await Guard(() => ScanOnceAsync(reset: false));
        _loadAll.Click += async (_, _) => await Guard(LoadAllAsync);
        _stop.Click += (_, _) => _scanCts?.Cancel();
        _create.Click += async (_, _) => await Guard(CreateKeyAsync);
        _pattern.TextChanged += (_, _) => ResetScan("MATCH 已变化，列表已清空。尚未开始。");
        _typeFilter.SelectedIndexChanged += (_, _) => ResetScan("类型过滤已变化，列表已清空。尚未开始。");
        _count.ValueChanged += (_, _) => ResetScan("COUNT 已变化，列表已清空。尚未开始。");

        _exactRow = new Panel { Dock = DockStyle.Top, Height = 36, Visible = false };
        _exact = new TextBox { Left = 8, Top = 4, Width = 280 };
        _exactRun = Theme.Button("查询", primary: true);
        _exactRun.Left = 294;
        _exactRun.Top = 2;
        _exactRun.Click += async (_, _) => await Guard(ExactQueryAsync);
        _exactRow.Controls.AddRange([_exact, _exactRun]);

        _keyList = new ListView { Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        _keyList.Columns.Add("键", 280);
        _keyList.Columns.Add("类型", 70);
        _keyList.RetrieveVirtualItem += (_, e) =>
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _keys.Count)
                return;
            var item = new ListViewItem(_keys[e.ItemIndex].Display);
            item.SubItems.Add(e.ItemIndex < _keyTypes.Count ? _keyTypes[e.ItemIndex] : "");
            e.Item = item;
        };
        _keyList.SelectedIndexChanged += async (_, _) => await Guard(SelectKeyAsync);
        _status = new Label { Dock = DockStyle.Bottom, Height = 42, ForeColor = Theme.Muted, Padding = new Padding(8, 4, 8, 4) };
        panel.Controls.Add(_keyList);
        panel.Controls.Add(_status);
        panel.Controls.Add(_exactRow);
        panel.Controls.Add(_scanRow);
        panel.Controls.Add(modes);
        Tip(_pattern, "MATCH 提示。含 * 时，开始查询会先确认。这是 SCAN，不是 KEYS。");
        Tip(_count, "COUNT 只是提示，不是返回条数。");
        Tip(_typeFilter, "TYPE 附加在同一次 SCAN 上。不会为了凑类型而连续扫描。");
        Tip(_start, "执行第一批 SCAN。连接后不会自动开始。");
        Tip(_more, "再执行一次 SCAN。空批且游标不是 0 仍可继续。");
        Tip(_loadAll, "确认后连续 SCAN 到游标 0 或你停止。内部批次不再确认。");
        Tip(_exact, "完整键名按字面查找。也可以输入 KEYS 加一个模式。");
        return panel;
    }

    private Control BuildDetail()
    {
        var panel = Column("键详情");
        _detailMeta = new Label { Dock = DockStyle.Top, Height = 72, Padding = new Padding(8), Text = "选中一个键后再读取值。" };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 0, 8, 0) };
        var refresh = Theme.Button("刷新");
        _saveString = Theme.Button("保存字符串");
        _moreString = Theme.Button("继续读取");
        _applyTtl = Theme.Button("设置 TTL");
        _ttlBox = new TextBox { Width = 70, Text = "-1" };
        _moreMembers = Theme.Button("加载更多成员");
        _saveMember = Theme.Button("保存成员");
        _addMember = Theme.Button("新增成员");
        _deleteMember = Theme.Button("删除成员");
        _deleteKey = Theme.Button("删除键");
        refresh.Click += async (_, _) => await Guard(SelectKeyAsync);
        _saveString.Click += async (_, _) => await Guard(SaveStringAsync);
        _moreString.Click += async (_, _) => await Guard(ContinueStringAsync);
        _applyTtl.Click += async (_, _) => await Guard(ApplyTtlAsync);
        _moreMembers.Click += async (_, _) => await Guard(() => LoadMembersAsync(append: true));
        _saveMember.Click += async (_, _) => await Guard(SaveMemberAsync);
        _addMember.Click += async (_, _) => await Guard(AddMemberAsync);
        _deleteMember.Click += async (_, _) => await Guard(DeleteMemberAsync);
        _deleteKey.Click += async (_, _) => await Guard(DeleteKeyAsync);
        buttons.Controls.AddRange([refresh, _saveString, _moreString, _ttlBox, _applyTtl, _moreMembers, _saveMember, _addMember, _deleteMember, _deleteKey]);
        _grid.SelectionChanged += (_, _) => ShowSelectedMember();
        var editors = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 0, 8, 0) };
        _fieldBox = new TextBox { Width = 120 };
        _valueEdit = new TextBox { Width = 180 };
        editors.Controls.Add(new Label { Text = "字段", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        editors.Controls.Add(_fieldBox);
        editors.Controls.Add(new Label { Text = "值", AutoSize = true, Padding = new Padding(6, 4, 4, 0) });
        editors.Controls.Add(_valueEdit);
        _valueBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9f) };
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Visible = false
        };
        panel.Controls.Add(_valueBox);
        panel.Controls.Add(_grid);
        panel.Controls.Add(editors);
        panel.Controls.Add(buttons);
        panel.Controls.Add(_detailMeta);
        Tip(_saveString, "只在已完整读入的 UTF-8 字符串上保存，并尽量保留原来的 TTL。没改内容不会写入。");
        Tip(_moreString, "继续 GETRANGE 下一段。没读完的字节不能保存。");
        Tip(_applyTtl, "只在秒数和当前显示不同时修改过期时间。");
        Tip(_saveMember, "只写入和当前成员不同的内容。集合改名先加入新成员，成功后再删旧成员。");
        Tip(_moreMembers, "Hash/Set/ZSet 再发一次游标命令；List 再取下一页 LRANGE。");
        Tip(_deleteKey, "单键 DEL，不是清库。");
        return panel;
    }

    private Control BuildTerminal()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.TerminalBg, Visible = false };
        _terminal = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = Theme.TerminalBg,
            ForeColor = Theme.TerminalFg,
            BorderStyle = BorderStyle.None,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            ScrollBars = ScrollBars.Vertical
        };
        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = Theme.TerminalBg };
        _terminalInput = new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x10, 0x14, 0x17), ForeColor = Theme.TerminalFg, BorderStyle = BorderStyle.FixedSingle };
        var run = Theme.Button("执行");
        var clear = Theme.Button("清屏");
        run.Dock = DockStyle.Right;
        clear.Dock = DockStyle.Right;
        run.Click += async (_, _) => await Guard(RunTerminalAsync);
        clear.Click += (_, _) => _terminal.Clear();
        _terminalInput.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await Guard(RunTerminalAsync);
            }
            else if (e.KeyCode == Keys.Up)
            {
                e.SuppressKeyPress = true;
                var previous = _history.Previous(_terminalInput.Text);
                if (previous != null)
                    _terminalInput.Text = previous;
            }
            else if (e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                var next = _history.Next();
                if (next != null)
                    _terminalInput.Text = next;
            }
        };
        inputRow.Controls.Add(_terminalInput);
        inputRow.Controls.Add(run);
        inputRow.Controls.Add(clear);
        panel.Controls.Add(_terminal);
        panel.Controls.Add(inputRow);
        Tip(_terminalInput, "Redis 命令。上下箭头翻看不含秘密的输入历史。");
        return panel;
    }

    private Control BuildAudit()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.Panel };
        _auditNote = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Text = "本地 JSONL，时间按 Asia/Shanghai 显示。不保存返回值。" };
        _auditList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None };
        _auditList.Columns.Add("时间", 140);
        _auditList.Columns.Add("位置", 140);
        _auditList.Columns.Add("命令", 320);
        _auditList.Columns.Add("结果", 60);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(8, 4, 8, 4) };
        var more = Theme.Button("加载更多记录");
        var clear = Theme.Button("清空本地记录");
        more.Click += (_, _) => { _auditSkip += AppLimits.AuditPageSize; ReloadAudit(); };
        clear.Click += (_, _) =>
        {
            if (!Dialogs.Confirm(this, "清空本地操作记录", "将删除程序目录里的操作记录文件，不影响 Redis。", danger: true))
                return;
            _audit.Clear();
            _auditSkip = 0;
            ReloadAudit();
        };
        buttons.Controls.Add(more);
        buttons.Controls.Add(clear);
        panel.Controls.Add(_auditList);
        panel.Controls.Add(buttons);
        panel.Controls.Add(_auditNote);
        return panel;
    }

    private static Panel Column(string title)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(0) };
        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Theme.Muted,
            BackColor = Color.FromArgb(0xF8, 0xF9, 0xFB)
        };
        panel.Controls.Add(label);
        return panel;
    }

    private void Tip(Control control, string text) => _tips.SetToolTip(control, text);

    private async Task Guard(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Mini Redis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            UpdateScanButtons();
        }
    }

    private void UpdateMode()
    {
        _readButton.BackColor = _readOnly ? Theme.ReadonlyBg : Theme.Panel;
        _readButton.ForeColor = _readOnly ? Theme.ReadonlyFg : Theme.Ink;
        _writeButton.BackColor = _readOnly ? Theme.Panel : Color.FromArgb(0xE7, 0xF3, 0xEF);
        _writeButton.ForeColor = _readOnly ? Theme.Ink : Theme.Accent;
        _create.Enabled = !_readOnly && _session != null;
        _saveString.Enabled = !_readOnly;
        _saveMember.Enabled = !_readOnly;
        _addMember.Enabled = !_readOnly;
        _deleteMember.Enabled = !_readOnly;
        _deleteKey.Enabled = !_readOnly;
        _applyTtl.Enabled = !_readOnly;
        RefreshWhere();
    }

    private void RefreshWhere()
    {
        var profile = SelectedProfile();
        var mode = _readOnly ? "只读" : "可写";
        if (profile == null)
        {
            _where.Text = mode;
            return;
        }

        var state = _session == null ? "未连接" : "已连接";
        _where.Text = mode + "  " + profile.Name + "  " + profile.Host + ":" + profile.Port + "  DB " + _database.Value + "  " + state;
    }

    private ConnectionProfile? SelectedProfile() =>
        _connList.SelectedIndex >= 0 && _connList.SelectedIndex < _profiles.Count ? _profiles[_connList.SelectedIndex] : null;

    private void ReloadConnections()
    {
        _connList.Items.Clear();
        foreach (var profile in _profiles)
            _connList.Items.Add(profile.Name + "  " + profile.Host + ":" + profile.Port);
    }

    private void OnConnectionSelected()
    {
        var profile = SelectedProfile();
        if (profile != null && _database.Value != profile.Database)
            _database.Value = Math.Min(_database.Maximum, profile.Database);
        if (_session != null)
            Disconnect();
        RefreshWhere();
    }

    private void EditConnection(ConnectionProfile? existing)
    {
        var draft = ConnectionEditor.Show(this, existing, _protector.IsAvailable, _protector.ScopeDescription);
        if (draft == null)
            return;
        var profile = draft.Profile;
        if (draft.PasswordTouched)
        {
            if (draft.SavePassword)
                _connections.SetPassword(profile, draft.TypedPassword, true, _protector);
            else
                _connections.SetPassword(profile, null, false, _protector);
            if (!string.IsNullOrEmpty(draft.TypedPassword))
                _passwords[profile.Id] = draft.TypedPassword;
            else
                _passwords.Remove(profile.Id);
        }

        var index = _profiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0)
            _profiles[index] = profile;
        else
            _profiles.Add(profile);
        _connections.Save(_profiles);
        ReloadConnections();
    }

    private void DeleteConnection()
    {
        var profile = SelectedProfile();
        if (profile == null)
            return;
        if (!Dialogs.Confirm(this, "删除连接", "从程序目录的配置中删除「" + profile.Name + "」。不会改动 Redis。", danger: true))
            return;
        if (_session != null)
            Disconnect();
        _profiles.RemoveAll(item => item.Id == profile.Id);
        _passwords.Remove(profile.Id);
        _connections.Save(_profiles);
        ReloadConnections();
    }

    private async Task ConnectAsync()
    {
        var profile = SelectedProfile();
        if (profile == null)
        {
            SetStatus("请先选择连接。");
            return;
        }

        Disconnect();
        var password = await ResolvePasswordAsync(profile);
        if (password == null && profile.SavePassword)
            return;
        _client = await StackExchangeRedisClient.ConnectAsync(profile, password, _lifetime.Token);
        try
        {
            _terminalClient = await StackExchangeRedisClient.ConnectAsync(profile, password, _lifetime.Token);
        }
        catch
        {
            _ = _client.DisposeAsync().AsTask();
            _client = null;
            throw;
        }

        _session = _client.Open((int)_database.Value);
        _terminalSession = new TrackedSession(_terminalClient, (int)_database.Value);
        _appliedDatabase = (int)_database.Value;
        var ping = await _runner.ExecuteAppAsync(_session, _audit, Context(profile), [RedisArg.FromText("PING")], _readOnly, _lifetime.Token);
        if (ping.IsError)
        {
            Disconnect();
            SetStatus(ping.ErrorText ?? "连接失败。");
            return;
        }

        ResetScan("已连接。尚未开始查询。");
        UpdateMode();
        NoteAuditFailure();
    }

    private async Task<string?> ResolvePasswordAsync(ConnectionProfile profile)
    {
        if (_passwords.TryGetValue(profile.Id, out var cached))
            return cached;
        var saved = _connections.TryUnprotect(profile, _protector, out var error);
        if (error != null)
            MessageBox.Show(this, error, "Mini Redis");
        if (!string.IsNullOrEmpty(saved))
        {
            _passwords[profile.Id] = saved;
            return saved;
        }

        if (!profile.SavePassword && string.IsNullOrEmpty(profile.Username))
        {
            var typed = Dialogs.Password(this, "密码（可留空，仅本次运行）");
            if (typed == null)
                return null;
            if (typed.Length > 0)
                _passwords[profile.Id] = typed;
            return typed;
        }

        var password = Dialogs.Password(this, "密码（仅本次运行，除非已用 DPAPI 保存）");
        if (password == null)
            return null;
        if (password.Length > 0)
            _passwords[profile.Id] = password;
        return password;
    }

    private void Disconnect()
    {
        _scanCts?.Cancel();
        _session = null;
        _terminalSession = null;
        var closing = _client;
        var closingTerminal = _terminalClient;
        _client = null;
        _terminalClient = null;
        if (closing != null)
            _ = closing.DisposeAsync().AsTask();
        if (closingTerminal != null)
            _ = closingTerminal.DisposeAsync().AsTask();
        ResetScan("已断开。");
        RefreshWhere();
    }

    private void ApplyDatabase(int database)
    {
        _terminalSession?.SetDatabase(database);
        if (_appliedDatabase == database)
        {
            RefreshWhere();
            return;
        }

        _appliedDatabase = database;
        if (_client != null)
            _session = _client.Open(database);
        if (_status != null)
            ResetScan("逻辑库已切换。尚未开始查询。");
        else
            RefreshWhere();
    }

    private void SetQueryMode(bool exact)
    {
        _exactMode = exact;
        _scanRow.Visible = !exact;
        _exactRow.Visible = exact;
        ResetScan(exact ? "精确查询不会自动执行。" : "尚未开始。");
    }

    private void ResetScan(string status)
    {
        _scanCts?.Cancel();
        _broker.Reset();
        _query = null;
        _keys.Clear();
        _keyTypes.Clear();
        _keyList.VirtualListSize = 0;
        _selected = null;
        _page = null;
        _members.Clear();
        _detailMeta.Text = "选中一个键后再读取值。";
        _valueBox.Clear();
        _grid.Rows.Clear();
        SetStatus(status);
        UpdateScanButtons();
    }

    private void UpdateScanButtons()
    {
        var session = _broker.Session;
        var canPage = _session != null && !_exactMode && session.Started && !session.Exhausted && !session.StoppedAtLimit && !_busy;
        _more.Enabled = canPage;
        _loadAll.Enabled = canPage || (_session != null && !_exactMode && session.Started && !session.Exhausted && !session.StoppedAtLimit);
        _stop.Enabled = _scanCts is { IsCancellationRequested: false };
        _create.Enabled = !_readOnly && _session != null;
    }

    private ScanQuery CurrentQuery()
    {
        var type = _typeFilter.SelectedIndex <= 0 ? null : _typeFilter.Text;
        return new ScanQuery
        {
            Pattern = string.IsNullOrWhiteSpace(_pattern.Text) ? "*" : _pattern.Text.Trim(),
            CountHint = (int)_count.Value,
            TypeFilter = type
        };
    }

    private async Task StartScanAsync()
    {
        if (_session == null)
        {
            SetStatus("尚未连接。");
            return;
        }

        var query = CurrentQuery();
        if (query.Pattern.Contains('*', StringComparison.Ordinal))
        {
            var body = WhereText() + "\n本次查询：SCAN MATCH「" + query.Pattern + "」"
                + (query.TypeFilter == null ? "" : " TYPE " + query.TypeFilter)
                + " COUNT " + query.CountHint
                + "\n\nMATCH 含 *。这是分批 SCAN，不是 KEYS，不会一次取回全部键。「加载更多」每次只再执行一次 SCAN，本轮不再为 * 单独确认。取消不会扫描。";
            if (!Dialogs.Confirm(this, "开始 SCAN", body, danger: false))
            {
                SetStatus("已取消 SCAN，列表未改动。");
                return;
            }
        }

        ResetScan("正在查询第一批。");
        _query = query;
        await ScanOnceAsync(reset: true);
    }

    private Task ScanOnceAsync(bool reset) => ScanOnceAsync(reset, token: null);

    private async Task ScanOnceAsync(bool reset, CancellationToken? token)
    {
        if (_session == null || _query == null)
            return;
        if (!reset && (!_broker.Session.Started || _broker.Session.Exhausted || _broker.Session.StoppedAtLimit))
            return;
        var used = token ?? NewScanToken();
        var generation = _broker.Generation;
        var sentCursor = _broker.Session.Cursor;
        var step = await _broker.ScanOnceAsync(_session, _query, generation, used);
        await AuditScanAsync(sentCursor, step);
        if (step.Discarded)
        {
            SetStatus("这批结果已过期，未写入列表。");
            return;
        }

        if (step.Failed)
        {
            SetStatus(SanitizeScanError(step.Error));
            return;
        }

        ApplyKeys();
        SetStatus("本批返回 " + step.Returned + " 个，新增 " + step.Added + " 个。 " + ScanStatus.Describe(_broker.Session));
    }

    private async Task LoadAllAsync()
    {
        if (_session == null)
            return;
        if (_query == null)
        {
            SetStatus("请先开始查询。加载全部从当前游标继续，不会隐式从头再扫一遍未确认的范围。");
            return;
        }

        var query = _query;
        var body = WhereText() + "\n从当前游标继续 SCAN MATCH「" + query.Pattern + "」"
            + (query.TypeFilter == null ? "" : " TYPE " + query.TypeFilter)
            + " COUNT " + query.CountHint
            + "\n\n将逐批扫描直到游标为 0 或你停止。这不是 KEYS。"
            + (query.Pattern.Contains('*', StringComparison.Ordinal) ? " MATCH 含 *，风险已并入这次确认，循环中不再弹窗。" : "")
            + " 没有虚构的总数。达到界面上限会停下来并说明尚未全部加载。";
        if (!Dialogs.Confirm(this, "加载全部", body, danger: false))
        {
            SetStatus("已取消加载全部。");
            return;
        }

        _ = query;
        var token = NewScanToken();
        try
        {
            while (_broker.Session.Started && !_broker.Session.Exhausted && !_broker.Session.StoppedAtLimit)
            {
                token.ThrowIfCancellationRequested();
                if (_broker.Session.Keys.Count >= AppLimits.MaxDisplayedKeys || _broker.Session.BatchCount >= AppLimits.MaxLoadAllBatches)
                {
                    _broker.Session.MarkLimit();
                    break;
                }

                var before = _broker.Session.BatchCount;
                await ScanOnceAsync(reset: false, token);
                if (_broker.Session.BatchCount == before)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("已停止。已保留已加载的键和游标，可以继续「加载更多」。这次没有记成扫描完成。");
            return;
        }

        SetStatus(ScanStatus.Describe(_broker.Session));
    }

    private CancellationToken NewScanToken()
    {
        _scanCts?.Cancel();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _scanCts.Token;
    }

    private async Task AuditScanAsync(string sentCursor, ScanStep step)
    {
        if (_query == null || step.Discarded || step.ScanCallsThisStep == 0)
            return;
        var args = ScanCommand.Build(_query, sentCursor);
        var echo = string.Join(' ', args.Select(arg => arg.Display));
        var clock = ShanghaiClock.Format(DateTimeOffset.UtcNow);
        var write = _audit.Append(new AuditEntry
        {
            Time = clock.Local,
            TimeUtc = clock.Utc,
            Connection = SelectedProfile()?.Name ?? "",
            Db = (int)_database.Value,
            Command = echo,
            Ok = !step.Failed,
            Error = step.Failed ? SanitizeScanError(step.Error) : ""
        });
        if (!write.Written)
            _auditNote.Text = write.Failure;
        await Task.CompletedTask;
    }

    private string SanitizeScanError(string error)
    {
        var parsed = CommandParser.FromTokens(["SCAN", _query?.Pattern ?? "*"]);
        return CommandSanitizer.SanitizeServerError(error, parsed, ArgVisibility.ShowAll);
    }

    private void ApplyKeys()
    {
        _keys.Clear();
        _keyTypes.Clear();
        foreach (var key in _broker.Session.Keys)
        {
            _keys.Add(key);
            _keyTypes.Add(_query?.TypeFilter ?? "");
        }

        _keyList.VirtualListSize = _keys.Count;
        _keyList.Invalidate();
    }

    private async Task ExactQueryAsync()
    {
        if (_session == null)
        {
            SetStatus("尚未连接。");
            return;
        }

        var request = ExactInput.Parse(_exact.Text, WhereText());
        if (request.Kind is ExactKind.Empty or ExactKind.Error)
        {
            SetStatus(request.Error ?? "无法查询。");
            return;
        }

        if (request.NeedsConfirmation && !Dialogs.Confirm(this, "确认查询", request.Confirmation, danger: request.Kind == ExactKind.Keys))
        {
            SetStatus("已取消查询，列表未改动。");
            return;
        }

        ResetScan("正在查询。");
        if (request.Kind == ExactKind.Literal)
        {
            var key = RedisArg.FromText(request.Text);
            var header = await ProbeAsync(key);
            _keys.Add(key);
            _keyTypes.Add(header.Type);
            _keyList.VirtualListSize = 1;
            SetStatus(header.Exists
                ? "已按字面定位「" + request.Text + "」。没有执行 SCAN 或 KEYS。"
                : "字面键不存在。没有执行 SCAN 或 KEYS。");
            return;
        }

        var reply = await ExecAsync(RedisArg.FromText("KEYS"), RedisArg.FromText(request.Text));
        if (reply.IsError)
        {
            SetStatus(reply.ErrorText ?? "KEYS 失败。");
            return;
        }

        var items = reply.Items ?? [];
        var dropped = 0;
        foreach (var item in items)
        {
            if (item.Bytes == null)
                continue;
            if (_keys.Count >= AppLimits.MaxDisplayedKeys)
            {
                dropped++;
                continue;
            }

            _keys.Add(new RedisArg(item.Bytes));
            _keyTypes.Add("");
        }

        _keyList.VirtualListSize = _keys.Count;
        SetStatus(dropped > 0
            ? "KEYS 已在服务器一次返回 " + items.Count + " 个匹配。界面只保留 " + _keys.Count + " 个，其余 " + dropped + " 个没有放进列表。这不是 SCAN，也不能靠分页减轻 KEYS 的成本。"
            : "KEYS 一次返回 " + _keys.Count + " 个匹配。这不是 SCAN，没有列表「加载更多」。");
    }

    private async Task SelectKeyAsync()
    {
        if (_keyList.SelectedIndices.Count == 0 || _session == null)
            return;
        var index = _keyList.SelectedIndices[0];
        if (index < 0 || index >= _keys.Count)
            return;
        _selected = _keys[index];
        var header = await ProbeAsync(_selected.Value);
        if (index < _keyTypes.Count)
            _keyTypes[index] = header.Type;
        _keyList.Invalidate();
        _keyHeadline = DescribeHeader(_selected.Value, header);
        _detailMeta.Text = _keyHeadline;
        _lastPttl = header.PttlMilliseconds;
        _ttlBox.Text = ValueGuard.TtlBoxText(header.PttlMilliseconds);
        _stringBuffer = new StringBuffer();
        _page = null;
        _members.Clear();
        _grid.Rows.Clear();
        _valueBox.Clear();
        if (!header.Exists || header.Error != null)
        {
            _valueBox.ReadOnly = true;
            return;
        }

        if (header.Type.Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            var view = await new KeyInspector(ExecListAsync).ReadStringAsync(_selected.Value, _lifetime.Token);
            _stringBuffer.Reset(view.Preview, view.TotalBytes);
            ShowString();
            return;
        }

        _valueBox.Visible = false;
        _grid.Visible = true;
        await LoadMembersAsync(append: false);
    }

    private async Task<KeyHeader> ProbeAsync(RedisArg key)
    {
        var inspector = new KeyInspector(ExecListAsync);
        return await inspector.ProbeAsync(key, _lifetime.Token);
    }

    private async Task LoadMembersAsync(bool append)
    {
        if (_selected == null || _session == null)
            return;
        var type = _keyTypes.ElementAtOrDefault(_keyList.SelectedIndices.Count > 0 ? _keyList.SelectedIndices[0] : -1) ?? "";
        if (string.IsNullOrEmpty(type))
        {
            var header = await ProbeAsync(_selected.Value);
            type = header.Type;
        }

        var cursor = append && _page != null ? _page.NextCursor : "0";
        var start = append && _page != null ? _page.NextIndex : 0;
        var page = await new KeyInspector(ExecListAsync).ReadCollectionAsync(
            _selected.Value, type, cursor, start, AppLimits.CollectionPageSize, _lifetime.Token);
        _page = page;
        if (!append)
        {
            _members.Clear();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            if (type == "hash")
            {
                _grid.Columns.Add("field", "字段");
                _grid.Columns.Add("value", "值");
            }
            else if (type == "zset")
            {
                _grid.Columns.Add("member", "成员");
                _grid.Columns.Add("score", "分数");
            }
            else if (type == "list")
            {
                _grid.Columns.Add("index", "下标");
                _grid.Columns.Add("value", "值");
            }
            else
            {
                _grid.Columns.Add("member", "成员");
            }
        }

        foreach (var row in page.Rows)
        {
            _members.Add(row);
            if (_grid.Columns.Count == 1)
                _grid.Rows.Add(row.Field.Display);
            else
                _grid.Rows.Add(row.Field.Display, row.Value?.Display ?? "");
        }

        var note = "基数命令与当前页不是同一个快照。保存只提交你改的成员，不会用本页替换整个集合。";
        if (page.Exhausted)
            note += " 这一侧的游标或范围已经到头。";
        else
            note += " 可以加载更多成员。";
        if (page.Error != null)
            note = page.Error;
        _detailMeta.Text = (_selected.Value.Display) + "  " + type + "  基数 " + page.Cardinality + "\n" + note;
    }

    private void ShowString()
    {
        _valueBox.Visible = true;
        _grid.Visible = false;
        _moreString.Enabled = _stringBuffer.CanContinue;
        _valueBox.ReadOnly = _readOnly || !_stringBuffer.CanEdit;
        if (_stringBuffer.CanEdit)
            _valueBox.Text = _stringBuffer.EditText;
        else if (_stringBuffer.IsUtf8)
            _valueBox.Text = Encoding.UTF8.GetString(_stringBuffer.Data);
        else
            _valueBox.Text = RedisText.Display(_stringBuffer.Data, 4096);
        var note = _keyHeadline;
        if (!_stringBuffer.IsComplete)
            note += "\n已读取 " + _stringBuffer.Data.Length + " / " + _stringBuffer.TotalBytes + " 字节。没读完的部分不会被保存覆盖。";
        if (_stringBuffer.HitLoadCap)
            note += "\n已达到界面读取上限，后面的字节没有读入，禁止保存。";
        else if (!_stringBuffer.IsUtf8)
            note += "\n当前内容不是可保存的 UTF-8 文本。二进制不会按文本写回。";
        _detailMeta.Text = note;
    }

    private async Task ContinueStringAsync()
    {
        if (_selected == null || !_stringBuffer.CanContinue)
            return;
        var offset = _stringBuffer.Data.LongLength;
        var end = offset + AppLimits.MaxStringBytes - 1;
        var reply = await ExecAsync(
            RedisArg.FromText("GETRANGE"),
            _selected.Value,
            RedisArg.FromText(offset.ToString(CultureInfo.InvariantCulture)),
            RedisArg.FromText(end.ToString(CultureInfo.InvariantCulture)));
        if (reply.IsError)
        {
            MessageBox.Show(this, reply.ErrorText);
            return;
        }

        _stringBuffer.Append(reply.Bytes ?? []);
        ShowString();
    }

    private async Task SaveStringAsync()
    {
        if (_readOnly || _selected == null)
            return;
        if (!_stringBuffer.CanEdit)
        {
            MessageBox.Show(this, "值还没读完，或不是 UTF-8。禁止保存，以免覆盖没看见的字节。");
            return;
        }

        if (ValueGuard.SameText(_stringBuffer.EditText, _valueBox.Text))
        {
            SetStatus("字符串没有改动，未写入。");
            return;
        }

        var note = _valueBox.Text.Length == 0 ? "新值是空字符串。" : "将覆盖当前字符串。";
        if (!Dialogs.Confirm(this, "保存字符串", note + "\n过期时间会保留。服务器若不支持 KEEPTTL，会在写入后按原来剩余的毫秒重新设置。取消不会写入。", danger: false))
            return;
        var reply = await ValueGuard.SaveStringPreservingTtlAsync(ExecListAsync, _selected.Value, _valueBox.Text, _lifetime.Token);
        if (reply.IsError)
            MessageBox.Show(this, reply.ErrorText);
        else
            await SelectKeyAsync();
    }

    private async Task ApplyTtlAsync()
    {
        if (_readOnly || _selected == null)
            return;
        var baseline = ValueGuard.TtlBoxText(_lastPttl);
        if (ValueGuard.SameText(baseline, _ttlBox.Text.Trim()))
        {
            SetStatus("TTL 没有改动，未写入。");
            return;
        }

        if (!long.TryParse(_ttlBox.Text.Trim(), out var seconds) || seconds == 0 || seconds < -1)
        {
            MessageBox.Show(this, "TTL 请填写正数秒，或 -1 表示取消过期。0 会删除键，这里不发送。");
            return;
        }

        var message = seconds < 0 ? "将取消这个键的过期时间。" : "将把过期时间改为 " + seconds + " 秒。";
        if (!Dialogs.Confirm(this, "修改 TTL", message + " 取消不会修改。", danger: false))
            return;
        if (seconds < 0)
            await ExecAsync(RedisArg.FromText("PERSIST"), _selected.Value);
        else
            await ExecAsync(RedisArg.FromText("PEXPIRE"), _selected.Value, RedisArg.FromText((seconds * 1000).ToString(CultureInfo.InvariantCulture)));
        await SelectKeyAsync();
    }

    private async Task AddMemberAsync()
    {
        if (_readOnly || _selected == null)
            return;
        var type = CurrentType();
        var empty = (type is "hash" or "set" or "zset" && _fieldBox.Text.Length == 0)
            || (type is "hash" or "list" && _valueEdit.Text.Length == 0);
        if (empty && !Dialogs.Confirm(this, "新增空成员", "字段或值是空的。确认后才会写入这个空内容。", danger: false))
            return;
        RedisReply reply;
        if (type == "hash")
            reply = await ExecAsync(RedisArg.FromText("HSET"), _selected.Value, RedisArg.FromText(_fieldBox.Text), RedisArg.FromText(_valueEdit.Text));
        else if (type == "list")
            reply = await ExecAsync(RedisArg.FromText("RPUSH"), _selected.Value, RedisArg.FromText(_valueEdit.Text));
        else if (type == "set")
            reply = await ExecAsync(RedisArg.FromText("SADD"), _selected.Value, RedisArg.FromText(_fieldBox.Text));
        else if (type == "zset")
            reply = await ExecAsync(RedisArg.FromText("ZADD"), _selected.Value, RedisArg.FromText(_valueEdit.Text), RedisArg.FromText(_fieldBox.Text));
        else
        {
            MessageBox.Show(this, "此类型不在界面里新增成员。");
            return;
        }

        if (reply.IsError)
            MessageBox.Show(this, reply.ErrorText);
        else
            await SelectKeyAsync();
    }

    private async Task SaveMemberAsync()
    {
        if (_readOnly || _selected == null || _grid.CurrentRow == null)
            return;
        var index = _grid.CurrentRow.Index;
        if (index < 0 || index >= _members.Count || !_members[index].CanEdit)
        {
            MessageBox.Show(this, "这一行不是可编辑的 UTF-8 成员，不会写回。");
            return;
        }

        var row = _members[index];
        var change = ValueGuard.DescribeMemberChange(
            CurrentType(),
            row.Field.IsUtf8 ? row.Field.Utf8 : "",
            row.Value is { IsUtf8: true } value ? value.Utf8 : "",
            _fieldBox.Text,
            _valueEdit.Text);
        if (change.Invalid)
        {
            MessageBox.Show(this, change.Message);
            return;
        }

        if (!change.Changed)
        {
            SetStatus("成员没有改动，未写入。");
            return;
        }

        if (!Dialogs.Confirm(this, "保存成员", change.Message + (change.EmptyValue ? " 新内容是空的。" : "") + " 取消不会写入。", danger: false))
            return;
        var reply = await ValueGuard.ApplyMemberChangeAsync(ExecListAsync, _selected.Value, change, _lifetime.Token);
        if (reply.IsError)
            MessageBox.Show(this, reply.ErrorText);
        else
            await SelectKeyAsync();
    }

    private void ShowSelectedMember()
    {
        if (_grid.CurrentRow == null)
            return;
        var index = _grid.CurrentRow.Index;
        if (index < 0 || index >= _members.Count)
            return;
        var row = _members[index];
        _fieldBox.Text = row.Field.IsUtf8 ? row.Field.Utf8 : "";
        _valueEdit.Text = row.Value is { IsUtf8: true } value ? value.Utf8 : "";
    }

    private async Task DeleteMemberAsync()
    {
        if (_readOnly || _selected == null || _grid.CurrentRow == null)
            return;
        var type = CurrentType();
        if (type == "list")
        {
            MessageBox.Show(this, "List 不在界面里按下标删除，以免用临时值误伤重复成员。需要时请在命令面板使用 LREM，并先确认。");
            return;
        }

        var row = _members[_grid.CurrentRow.Index];
        var command = type switch
        {
            "hash" => "HDEL",
            "set" => "SREM",
            "zset" => "ZREM",
            _ => ""
        };
        if (command.Length == 0)
            return;
        if (!Dialogs.Confirm(this, "删除成员", "只删除已选中的这个成员，不会重写整个集合。", danger: true))
            return;
        await ExecAsync(RedisArg.FromText(command), _selected.Value, row.Field);
        await SelectKeyAsync();
    }

    private async Task DeleteKeyAsync()
    {
        if (_readOnly || _selected == null)
            return;
        if (!Dialogs.Confirm(this, "删除键", WhereText() + "\n将 DEL 单键「" + _selected.Value.Display + "」。这不是清库。", danger: true))
            return;
        await ExecAsync(RedisArg.FromText("DEL"), _selected.Value);
        _keys.RemoveAll(item => item.Identity == _selected.Value.Identity);
        _keyList.VirtualListSize = _keys.Count;
        _selected = null;
        _detailMeta.Text = "键已删除。列表没有重新 SCAN。";
    }

    private async Task CreateKeyAsync()
    {
        if (_readOnly || _session == null)
            return;
        var draft = NewKeyEditor.Show(this);
        if (draft == null)
            return;
        var key = RedisArg.FromText(draft.Name);
        var exists = await ExecAsync(RedisArg.FromText("EXISTS"), key);
        if ((exists.Integer ?? 0) > 0)
        {
            MessageBox.Show(this, "键已存在，没有覆盖。");
            return;
        }

        RedisReply reply = draft.Type switch
        {
            "hash" => await ExecAsync(RedisArg.FromText("HSET"), key, RedisArg.FromText(draft.Field), RedisArg.FromText(draft.Value)),
            "list" => await ExecAsync(RedisArg.FromText("RPUSH"), key, RedisArg.FromText(draft.Value)),
            "set" => await ExecAsync(RedisArg.FromText("SADD"), key, RedisArg.FromText(draft.Value)),
            "zset" => await ExecAsync(RedisArg.FromText("ZADD"), key, RedisArg.FromText(draft.Score), RedisArg.FromText(draft.Value)),
            _ => await ExecAsync(RedisArg.FromText("SET"), key, RedisArg.FromText(draft.Value))
        };
        if (reply.IsError)
        {
            MessageBox.Show(this, reply.ErrorText);
            return;
        }

        if (draft.TtlSeconds > 0)
            await ExecAsync(RedisArg.FromText("PEXPIRE"), key, RedisArg.FromText((draft.TtlSeconds * 1000L).ToString(CultureInfo.InvariantCulture)));
        SetStatus("已创建键。列表没有自动重新扫描。");
    }

    private async Task RunTerminalAsync()
    {
        if (_terminalSession == null)
        {
            AppendTerminal("尚未连接。");
            return;
        }

        var line = _terminalInput.Text;
        var profile = SelectedProfile();
        var first = await _runner.RunTerminalAsync(_terminalSession, _audit, Context(profile), line, _readOnly, confirmed: false, _lifetime.Token);
        if (first.Command != null)
            _history.Accept(first.Command, first.Visibility);
        if (first.NeedsConfirmation)
        {
            var body = WhereText() + "\n" + first.Echo + "\n" + first.Output;
            if (!Dialogs.Confirm(this, "确认执行", body, danger: true))
            {
                AppendTerminal("> " + first.Echo + "\n已取消，未发送。");
                return;
            }

            first = await _runner.RunTerminalAsync(_terminalSession, _audit, Context(profile), line, _readOnly, confirmed: true, _lifetime.Token);
        }

        if (_terminalSession.Database != (int)_database.Value)
            _database.Value = _terminalSession.Database;

        if (first.Command?.Name is "CLEAR" or "CLS")
        {
            _terminal.Clear();
            return;
        }

        var authNote = first.Sent && first.Command?.Name is "AUTH" or "HELLO"
            ? "\n这条命令只作用在命令面板自己的连接上，界面连接的登录没有改。"
            : "";
        AppendTerminal("> " + first.Echo + "\n" + first.Output + authNote);
        if (first.AuditFailed)
            _auditNote.Text = first.AuditFailure ?? "操作记录写入失败。命令结果以上面的回显为准。";
        _terminalInput.Clear();
    }

    private void AppendTerminal(string text)
    {
        var next = _terminal.Text.Length == 0 ? text : _terminal.Text + "\n" + text;
        if (next.Length > AppLimits.ReplyMaxChars)
            next = next[^AppLimits.ReplyMaxChars..] + "\n… 终端显示已截断。";
        _terminal.Text = next;
        _terminal.SelectionStart = _terminal.Text.Length;
        _terminal.ScrollToCaret();
    }

    private async Task<RedisReply> ExecAsync(params RedisArg[] args)
    {
        if (_session == null)
            return RedisReply.ErrorOf("尚未连接。");
        var reply = await _runner.ExecuteAppAsync(_session, _audit, Context(SelectedProfile()), args, _readOnly, _lifetime.Token);
        NoteAuditFailure();
        return reply;
    }

    private Task<RedisReply> ExecListAsync(IReadOnlyList<RedisArg> args, CancellationToken cancellationToken)
    {
        if (_session == null)
            return Task.FromResult(RedisReply.ErrorOf("尚未连接。"));
        return _runner.ExecuteAppAsync(_session, _audit, Context(SelectedProfile()), args, _readOnly, cancellationToken);
    }

    private void NoteAuditFailure()
    {
        var page = _audit.ReadRecent(0, 1);
        if (page.ReadWarning != null)
            _auditNote.Text = page.ReadWarning;
    }

    private void ReloadAudit()
    {
        var page = _audit.ReadRecent(_auditSkip, AppLimits.AuditPageSize);
        _auditList.Items.Clear();
        foreach (var entry in page.Entries)
        {
            var item = new ListViewItem(entry.Time);
            item.SubItems.Add(entry.Connection + " / db" + entry.Db);
            item.SubItems.Add(entry.Command);
            item.SubItems.Add(entry.Ok ? "成功" : "失败");
            _auditList.Items.Add(item);
        }

        _auditNote.Text = "程序目录 logs/audit.jsonl。时间是 Asia/Shanghai。不保存返回值。"
            + (page.SkippedMalformed ? " 有无法解析的行已跳过。" : "")
            + (page.ReadWarning ?? "");
    }

    private AuditContext Context(ConnectionProfile? profile = null) =>
        new(profile?.Name ?? SelectedProfile()?.Name ?? "", (int)_database.Value);

    private string WhereText()
    {
        var profile = SelectedProfile();
        return profile == null
            ? "未选择连接"
            : profile.Name + " / " + profile.Host + ":" + profile.Port + " / db" + _database.Value;
    }

    private string CurrentType()
    {
        if (_keyList.SelectedIndices.Count == 0)
            return "";
        var index = _keyList.SelectedIndices[0];
        return index >= 0 && index < _keyTypes.Count ? _keyTypes[index] : "";
    }

    private static string DescribeHeader(RedisArg key, KeyHeader header)
    {
        if (header.Error != null)
            return key.Display + "\n" + header.Error;
        var ttl = header.PttlMilliseconds switch
        {
            -2 => "键不存在",
            -1 => "无过期",
            _ => header.PttlMilliseconds + " 毫秒"
        };
        return key.Display + "\nTYPE " + header.Type + "    PTTL " + ttl;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void ToggleRow(int row, int height)
    {
        var open = _root.RowStyles[row].Height < 10;
        _root.RowStyles[row].Height = open ? height : 0;
        if (_root.GetControlFromPosition(0, row) is Control panel)
            panel.Visible = open;
    }

    private void ShowHelp()
    {
        using var form = new Form
        {
            Text = "使用说明",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(520, 640),
            Font = Theme.Ui()
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = HelpCopy,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Panel
        };
        form.Controls.Add(box);
        form.ShowDialog(this);
    }

    private void Shutdown()
    {
        _lifetime.Cancel();
        _scanCts?.Cancel();
        if (_client != null)
            _ = _client.DisposeAsync().AsTask();
    }

    private const string HelpCopy =
        "Mini Redis 连接你填写的 Redis，用来查看和编辑键。它不是 Redis 官方产品，也没有内嵌浏览器。\n\n"
        + "配置和操作记录只在程序目录的 data 与 logs 里。目录不可写时会直接提示，不会改存到 AppData。文件夹可以搬走。保存的密码使用当前 Windows 用户的 DPAPI，换机器或换用户不能解开。\n\n"
        + "默认只读，每次启动都是只读。连接成功后不会自动 SCAN。\n\n"
        + "SCAN：「开始查询」发第一批。「加载更多」每次只再发一条 SCAN。COUNT 不是返回条数。空批且游标不是 0 不算结束。游标按无符号整数原样保存。类型过滤是同一条 SCAN 的 TYPE，服务器不支持就会报错，不会暗中连扫多批。SCAN 不是快照。\n\n"
        + "「加载全部」只确认一次，然后连续 SCAN 直到游标为 0 或你停止。进度没有虚构总数。到达界面上限会停下，并说明还没有全部加载。\n\n"
        + "精确查询：完整键名只做 EXISTS / TYPE / PTTL，键名里的 * ? [] 都是字面值。输入 KEYS 加模式才会执行 KEYS。KEYS 即使没有 * 也要确认，它是一次阻塞的全库匹配，分页不能减轻服务器成本。\n\n"
        + "选中键之后才读值。大字符串会截断并禁止保存。非 UTF-8 只读。集合按页读取，保存不会用当前页覆盖整个集合。\n\n"
        + "命令面板是 Redis 命令，不是操作系统命令。它使用单独的连接。SELECT 成功后界面库号会跟着变，之后的界面写入发到那个库；不会靠把界面连接的内部库状态改乱来实现。MULTI 和 EXEC 之间，命令面板的下一条命令会进入事务，QUEUED 只表示入队，不是已经写入。界面命令不进入这条事务。AUTH 只改变命令面板那条连接。\n\n"
        + "所用客户端把 KEYS 视为管理命令，连接时因此放行库自身的管理命令检查，否则确认后的 KEYS 也发不出去。这不是安全开关。FLUSHDB、FLUSHALL、SHUTDOWN 仍在发送前直接拒绝。只读模式只放行 COMMAND 元数据标成 readonly 且没有 write 的命令。可写模式仍会转发未知命令。EVAL、FCALL 和模块命令可能间接执行被硬拒绝的操作，需要在服务器 ACL 里限制。\n\n"
        + "操作记录只留脱敏后的命令和成败，不留返回值。凭据、脚本、未知参数和写入值会隐藏；服务器错误里如果可能带回这些内容，也会隐藏。";
}
