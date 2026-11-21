using System.Drawing;
using System.Windows.Forms;

namespace RimWise.MarketplaceReview;

internal sealed class ReviewLoginDialog : Form
{
    private readonly TextBox username = new() { Dock = DockStyle.Fill, MaxLength = 32 };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 128 };

    internal string Username => username.Text.Trim();
    internal string Password => password.Text;

    internal ReviewLoginDialog(string savedUsername)
    {
        Text = "登录排序市场审核账号";
        Size = new Size(430, 245);
        MinimumSize = Size;
        MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        BackColor = Color.FromArgb(20, 29, 33);
        ForeColor = Color.FromArgb(226, 235, 236);
        Font = new Font("Microsoft YaHei UI", 10);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        username.Text = savedUsername;
        AddField(layout, "用户名", username, 0);
        AddField(layout, "密码", password, 1);
        var hint = new Label { Text = "仅审核员账号可访问待审投稿。密码由 Windows 凭据管理器保护。", Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(145, 171, 175), TextAlign = ContentAlignment.MiddleLeft, AutoSize = false };
        layout.Controls.Add(hint, 0, 2); layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var login = new ReviewButton { Text = "登录", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new ReviewButton { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([login, cancel]); layout.Controls.Add(buttons, 0, 3); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout); AcceptButton = login; CancelButton = cancel;
        Shown += (_, _) => { if (username.Text.Length > 0) password.Focus(); else username.Focus(); };
    }

    private static void AddField(TableLayoutPanel layout, string label, Control field, int row)
    {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        field.Margin = new Padding(0, 6, 0, 6);
        field.BackColor = Color.FromArgb(29, 42, 47);
        field.ForeColor = Color.FromArgb(226, 235, 236);
        layout.Controls.Add(field, 1, row);
    }
}

internal sealed class ReviewReasonDialog : Form
{
    private readonly TextBox reason = new() { Dock = DockStyle.Fill, Multiline = true, MaxLength = 4000, ScrollBars = ScrollBars.Vertical };
    internal string Reason => reason.Text.Trim();

    internal ReviewReasonDialog()
    {
        Text = "退回投稿";
        Size = new Size(520, 340);
        MinimumSize = Size;
        MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        BackColor = Color.FromArgb(20, 29, 33);
        ForeColor = Color.FromArgb(226, 235, 236);
        Font = new Font("Microsoft YaHei UI", 10);
        reason.BackColor = Color.FromArgb(29, 42, 47);
        reason.ForeColor = Color.FromArgb(226, 235, 236);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Text = "请填写退回原因，作者可在修改后重新提交。", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(reason, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var submit = new ReviewButton { Text = "确认退回", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new ReviewButton { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([submit, cancel]); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout); AcceptButton = submit; CancelButton = cancel;
    }
}
