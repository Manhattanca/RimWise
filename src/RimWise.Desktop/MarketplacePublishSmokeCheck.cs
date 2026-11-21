#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Marketplace;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Synthetic publish interactions only. No OAuth endpoint, browser or remote writes.</summary>
public static class MarketplacePublishSmokeCheck
{
    public static async Task RunAsync(MainWindow window, string output, List<string> checks)
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }

        var snapshot = new MarketplaceManifest(1, "publish-fixture", "1.0.0", "发布验收 · 合成排序", "unpublished",
            "仅用于本地验收。", "1.7", [], null,
            [new("ludeon.rimworld", null), new("example.fixture", "123456")]) { ReleaseNotes = "初始版本说明" };
        MarketplaceManifest? saved = null;
        var logins = 0;
        var submissions = 0;
        var opened = 0;
        var loggedOut = false;
        var dialogs = new List<MarketplacePublishWindow>();
        MarketplacePublishWindow Show(MarketplacePublishActions actions, MarketplaceManifest? draft = null)
        {
            var dialog = new MarketplacePublishWindow(draft ?? snapshot, actions) { Owner = window };
            dialogs.Add(dialog);
            dialog.Show();
            return dialog;
        }

        MarketplacePublishActions SignedOutActions(string id = "") => new()
        {
            ClientId = id,
            ClientSecretConfigured = false,
            LoginAsync = (_, _, _) => { logins++; throw new InvalidOperationException("Unexpected login callback"); },
            SubmitAsync = (_, _, _) => { submissions++; throw new InvalidOperationException("Unexpected submit callback"); },
            Logout = () => loggedOut = true,
            OpenTrustedLink = _ => opened++,
            SaveDraft = draft => saved = draft,
            LoadDraft = () => saved
        };

        try
        {
            var imagePath = Path.Combine(output, "fixture.png");
            Directory.CreateDirectory(output);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8)));
            using (var stream = File.Create(imagePath)) encoder.Save(stream);
            var jpegEncoder = new System.Windows.Media.Imaging.JpegBitmapEncoder(); jpegEncoder.Frames.Add(encoder.Frames[0]);
            using (var jpegStream = new MemoryStream())
            {
                jpegEncoder.Save(jpegStream); var jpeg = jpegStream.ToArray();
                Check(MarketplaceImages.Optimize(jpeg).SequenceEqual(jpeg), "JPEG remains byte-identical without lossy re-encoding");
            }
            var imageStore = new MarketplaceDraftStore(Path.Combine(output, "image-drafts"));
            var imageKey = Guid.NewGuid().ToString("N");
            var imageActions = SignedOutActions();
            var imageDialog = Show(new MarketplacePublishActions
            {
                LoginAsync = imageActions.LoginAsync, SubmitAsync = imageActions.SubmitAsync,
                Logout = imageActions.Logout, OpenTrustedLink = imageActions.OpenTrustedLink,
                SaveImageDraft = (draft, images) => imageStore.Save(imageKey, draft, false, images)
            });
            await imageDialog.AddImagesAsync([imagePath, imagePath, imagePath]);
            Check(((System.Windows.Controls.WrapPanel)imageDialog.FindName("ImageList")).Children.Count == 3, "Three images preview without changing source files");
            Check(imageStore.ReadAll().Count == 0, "Selecting images never auto-saves drafts");
            await imageDialog.AddImagesAsync([imagePath]);
            Check(((System.Windows.Controls.WrapPanel)imageDialog.FindName("ImageList")).Children.Count == 3, "Fourth image rejected without losing selected images");
            imageDialog.SaveDraftNow();
            var imageDraft = imageStore.ReadAll().Single();
            Check(imageDraft.ImageContents.Count == 3, "Explicit draft save preserves all image bytes");
            var restoredImages = Show(new MarketplacePublishActions
            {
                LoginAsync = imageActions.LoginAsync, SubmitAsync = imageActions.SubmitAsync,
                Logout = imageActions.Logout, OpenTrustedLink = imageActions.OpenTrustedLink,
                InitialImages = imageDraft.ImageContents, LoadDraft = () => imageDraft.Manifest
            });
            Check(((System.Windows.Controls.WrapPanel)restoredImages.FindName("ImageList")).Children.Count == 3, "Reopened image draft restores three previews");
            imageDialog.Close(); restoredImages.Close(); imageStore.Delete(imageKey);
            Check(imageStore.ReadAll().Count == 0, "Deleting image draft removes stored image bytes");
            var imageSubmissions = 0;
            var unexpectedSaves = 0;
            var uploadDialog = Show(new MarketplacePublishActions
            {
                InitialUser = new("FixtureAuthor", null),
                LoginAsync = imageActions.LoginAsync, SubmitAsync = imageActions.SubmitAsync,
                Logout = imageActions.Logout, OpenTrustedLink = imageActions.OpenTrustedLink,
                InitialImages = imageDraft.ImageContents,
                SaveImageDraft = (_, _) => unexpectedSaves++,
                SubmitImagesAsync = (draft, images, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Check(images.Count == 3 && draft.Images.SequenceEqual(images.Select(image => image.Image)),
                        "Image upload manifest uses the automatically processed upload bytes");
                    foreach (var image in images) MarketplaceImages.Verify(image.Image, image.Content);
                    Check(images.All(image => image.Content.Length <= MarketplaceImages.MaximumBytes),
                        "Automatically processed upload images pass current server-side format and hash rules");
                    imageSubmissions++;
                    return Task.FromResult(new MarketplaceSubmission(16,
                        new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/16"), "fixture", "fixture.json", new string('a', 64)));
                }
            });
            var captions = ((System.Windows.Controls.WrapPanel)uploadDialog.FindName("ImageList")).Children
                .OfType<System.Windows.Controls.StackPanel>().SelectMany(panel => panel.Children.OfType<System.Windows.Controls.TextBlock>()).ToArray();
            Check(captions.Select(caption => caption.Text).SequenceEqual(["封面", "图片 2", "图片 3"]),
                "Image labels show neither original nor compressed file sizes");
            await uploadDialog.SubmitAsync();
            Check(imageSubmissions == 1 && unexpectedSaves == 0 && uploadDialog.SubmissionPanel.Visibility == Visibility.Visible,
                "Automatic image compression completes submission without saving or overwriting a draft");
            uploadDialog.Close();
            var unrefreshed = new MainWindow(initializePaths: false);
            using var unrefreshedXml = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7.0</version><activeMods><li>ludeon.rimworld</li><li>example.fixture</li></activeMods></ModsConfigData>"));
            var unrefreshedFile = new ModsConfigXmlFormat().Read(unrefreshedXml);
            unrefreshed.ViewModel.Load(unrefreshedFile, "synthetic-unrefreshed.xml");
            unrefreshed.ViewModel.IsScanning = true;
            unrefreshed.ViewModel.IsShowingCache = true;
            var unrefreshedDraft = unrefreshed.CreateMarketplaceDraft();
            Check(unrefreshedDraft.Mods.Select(mod => mod.PackageId).SequenceEqual(new[] { "ludeon.rimworld", "example.fixture" })
                && unrefreshedDraft.Mods[1].WorkshopId is null,
                "Draft opens without catalogue while scanning or showing cache and preserves unresolved order for later verification");
            unrefreshed.ViewModel.MarkExported();
            unrefreshed.Close();

            await CheckFilePublishingAsync(window, output, checks);

            var verifiedSaves = 0;
            var verifiedSubmissions = 0;
            MarketplaceManifest? submittedUnrefreshed = null;
            var verification = Show(new MarketplacePublishActions
            {
                InitialUser = new("FixtureAuthor", null),
                LoginAsync = (_, _, _) => throw new InvalidOperationException("Unexpected login callback"),
                SubmitAsync = (draft, _, _) => { verifiedSubmissions++; submittedUnrefreshed = draft; return Task.FromResult(new MarketplaceSubmission(12,
                    new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/12"), "fixture", "fixture.json", new string('a', 64))); },
                SaveDraft = draft => { verifiedSaves++; saved = draft; },
                Logout = () => { }, OpenTrustedLink = _ => { }
            }, unrefreshedDraft);
            Check(verification.TitleBox.IsEnabled,
                "Opening publication editor never scans installed mods or blocks editing");
            verification.TitleBox.Text = "未刷新时填写的草稿";
            verification.DescriptionBox.Text = "未刷新时填写的排序简介。";
            await Task.Delay(1000);
            Check(verifiedSaves == 0, "Editing and waiting beyond the former debounce interval never writes a draft");
            verification.SaveDraftButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(verifiedSaves == 1,
                "Saving a draft never scans installed workshop sources or requires login");
            await verification.SaveLocalDraftAsync();
            Check(verifiedSaves == 2 && verification.TitleBox.Text == "未刷新时填写的草稿"
                && verification.SaveDraftButton.IsEnabled, "Local save remains available without a mod catalogue");
            await verification.SubmitAsync();
            Check(verifiedSubmissions == 1 && verification.SubmissionPanel.Visibility == Visibility.Visible
                && submittedUnrefreshed?.Mods.SequenceEqual(unrefreshedDraft.Mods) == true,
                "Unrefreshed order submits unchanged without local Workshop source verification");
            Check(verifiedSaves == 2, "Successful submission never saves a draft automatically");
            verification.TitleBox.Text = "关闭前的未保存修改";
            verification.Close();
            await Task.Delay(800);
            Check(verifiedSaves == 2 && saved?.Title == "未刷新时填写的草稿", "Closing an editor discards unsaved changes without overwriting the last explicit save");

            saved = null;
            var signedOut = Show(SignedOutActions());
            await Idle(signedOut);
            Check(signedOut.FindName("ClientIdBox") is null && signedOut.FindName("ClientSecretBox") is null,
                "OAuth application credentials are supplied by the build and are not editable in the author window");
            Check(signedOut.FindName("IdBox") is null && signedOut.PublishedIdPanel.Visibility == Visibility.Collapsed
                && signedOut.PublishedIdText.Text.Length == 0 && signedOut.Draft.Id == snapshot.Id,
                "New publication keeps the generated identifier internal until approval");
            Check(signedOut.FindName("UpdateExistingCheck") is null && signedOut.ReleaseNotesPanel.Visibility == Visibility.Collapsed,
                "New publication has neither an update checkbox nor a release notes field");
            Check(!signedOut.LoginButton.IsEnabled && !signedOut.SubmitButton.IsEnabled && signedOut.SaveDraftButton.IsEnabled
                && signedOut.LoginStatus.Text == "此版本未配置 GitHub 发布登录，可先保存本地草稿。",
                "Missing owner OAuth configuration disables login and submit while keeping local draft saving available");
            await signedOut.LoginAsync();
            await signedOut.SubmitAsync();
            Check(logins == 0 && submissions == 0, "Unconfigured author cannot start OAuth or remote submission");
            signedOut.TitleBox.Text = "";
            signedOut.ReleaseNotesBox.Text = "  未完成草稿中的版本说明  ";
            signedOut.SaveDraftNow();
            Check(saved is { Title: "", ReleaseNotes: "" }, "New publication draft ignores hidden release notes while preserving incomplete text");
            signedOut.TitleBox.Text = "恢复草稿 · 自定义标题";
            signedOut.VersionBox.Text = "2.0.0";
            signedOut.SaveDraftNow();
            signedOut.CollectionBox.Text = "尚未填完的合集链接";
            await signedOut.SaveLocalDraftAsync();
            Check(saved?.WorkshopCollectionId == "尚未填完的合集链接" && logins == 0, "Incomplete collection input can be saved locally without publication validation");
            signedOut.CollectionBox.Text = "";
            signedOut.SaveDraftNow();
            signedOut.TitleBox.Text = "未保存标题不得覆盖";
            signedOut.Close();

            MarketplaceManifest? collisionSubmission = null;
            var collisionOrder = new List<string>();
            var collisionCheck = Show(new MarketplacePublishActions
            {
                LoginAsync = (_, _, _) => throw new InvalidOperationException("Unexpected login callback"),
                InitialUser = new("FixtureAuthor", null),
                SubmitAsync = (draft, _, _) => { collisionOrder.Add("submit"); collisionSubmission = draft; return Task.FromResult(new MarketplaceSubmission(13,
                    new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/13"), "fixture", "fixture.json", new string('a', 64))); },
                LoadMarketplaceIdsAsync = _ => { collisionOrder.Add("check"); return Task.FromResult<IReadOnlyList<string>>(["taken-id"]); },
                Logout = () => { }, OpenTrustedLink = _ => { }
            }, snapshot with { Id = "taken-id" });
            await collisionCheck.SubmitAsync();
            Check(collisionSubmission is not null && collisionSubmission.Id != "taken-id"
                && collisionCheck.Draft.Id == collisionSubmission.Id && !collisionCheck.PublishedIdPanel.IsVisible
                && collisionOrder.SequenceEqual(["check", "submit"]),
                "A market-index collision regenerates the internal identifier before submission without source verification");
            collisionCheck.Close();

            var restored = Show(SignedOutActions(), snapshot with { Id = "fresh-random-id" });
            Check(restored.Draft.Id == snapshot.Id && restored.TitleBox.Text == "恢复草稿 · 自定义标题"
                && restored.VersionBox.Text == "2.0.0" && restored.ReleaseNotesBox.Text == ""
                && restored.Draft.Mods.SequenceEqual(snapshot.Mods),
                "Reopening the same order restores edited identity, title, version and release notes while preserving the snapshot");
            restored.Close();

            var different = Show(SignedOutActions(), snapshot with
            { Mods = [new("ludeon.rimworld", null), new("example.other", "123457")] });
            Check(different.Draft.Id == snapshot.Id && different.TitleBox.Text == snapshot.Title,
                "Saved draft metadata is not restored onto a different captured mod order");
            different.Close();
            saved = null;

            var loginResult = new TaskCompletionSource<GitHubMarketplaceUser>(TaskCreationOptions.RunContinuationsAsynchronously);
            var submitResult = new TaskCompletionSource<MarketplaceSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
            MarketplaceManifest? submitted = null;
            var login = Show(new MarketplacePublishActions
            {
                IsUpdate = true,
                ClientId = "synthetic-public-client",
                ClientSecretConfigured = true,
                LoginAsync = async (_, openBrowser, token) =>
                {
                    logins++;
                    token.ThrowIfCancellationRequested();
                    openBrowser(new Uri("https://github.com/login/oauth/authorize?client_id=fixture"));
                    return await loginResult.Task.WaitAsync(token);
                },
                SubmitAsync = (draft, progress, token) =>
                {
                    submissions++;
                    submitted = draft;
                    progress.Report("模拟提交等待完成");
                    return submitResult.Task.WaitAsync(token);
                },
                Logout = () => loggedOut = true,
                OpenTrustedLink = _ => opened++,
                SaveDraft = draft => saved = draft
            }, snapshot with { Author = "FixtureAuthor" });
            Check(login.ReleaseNotesPanel.Visibility == Visibility.Visible, "Update entry shows optional release notes");
            Check(login.PublishedIdPanel.IsVisible && login.PublishedIdText.Text == snapshot.Id,
                "An approved order retains its visible identifier in the update entry");
            login.ReleaseNotesBox.Text = "  版本说明必须保留  ";
            var loginTask = login.LoginAsync();
            Check(login.IsBusy && opened == 1 && login.CancelButton.Visibility == Visibility.Visible && !login.SubmitButton.IsEnabled,
                "Browser authorization opens GitHub and waits with a working cancel action");
            loginResult.SetResult(new("FixtureAuthor", "Synthetic author"));
            await loginTask;
            Check(!login.IsBusy && login.SubmitButton.IsEnabled && login.Draft.Author == "FixtureAuthor"
                && login.AuthorStatus.Text.Contains("FixtureAuthor") && login.Draft.ReleaseNotes == "版本说明必须保留"
                && login.LoginStatus.Text.Contains("当前 GitHub 账号"),
                "Completed injected login sets the authenticated author and preserves release notes");
            var submitTask = login.SubmitAsync();
            Check(login.IsBusy && submitted is { Author: "FixtureAuthor", ReleaseNotes: "版本说明必须保留" }
                && submitted.Mods.SequenceEqual(snapshot.Mods) && !login.SubmitButton.IsEnabled,
                "Submission receives the authenticated snapshot once and disables duplicate submission while pending");
            submitResult.SetResult(new(12345, new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/12345"),
                "synthetic-branch", "marketplace/manifests/fixtureauthor/publish-fixture/1.0.0.json", new string('a', 64)));
            await submitTask;
            await Idle(login);
            Check(login.SubmissionPanel.Visibility == Visibility.Visible && login.SubmissionLinkText.Text.Contains("12345")
                && login.FindName("SubmissionIdText") is null
                && !login.SubmitButton.IsEnabled && login.StatusText.Text.Contains("等待审核"),
                "Completed submission remains visibly pending review without another identifier field");
            Check(submissions == 1 && logins == 1 && opened == 1, "Synthetic login opens the validated GitHub authorization page exactly once without remote submissions");
            login.LogoutButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(loggedOut && login.Draft.Author == "FixtureAuthor" && !login.SubmitButton.IsEnabled,
                "Logout clears the authenticated author while retaining the draft");
            login.Close();

            var lateResult = new TaskCompletionSource<GitHubMarketplaceUser>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<Uri>? lateBrowser = null;
            var closing = Show(new MarketplacePublishActions
            {
                ClientId = "synthetic-public-client",
                ClientSecretConfigured = true,
                LoginAsync = (_, openBrowser, _) => { lateBrowser = openBrowser; return lateResult.Task; },
                SubmitAsync = (_, _, _) => throw new InvalidOperationException("Unexpected submission"),
                Logout = () => { }, OpenTrustedLink = _ => opened++
            });
            var closingTask = closing.LoginAsync();
            closing.Cancel();
            closing.Close();
            Check(closing.IsVisible && closing.IsBusy, "Closing a busy dialog cancels and waits for its active callback");
            var lateOpenIgnored = false;
            try { lateBrowser!(new Uri("https://github.com/login/oauth/authorize")); }
            catch (OperationCanceledException) { lateOpenIgnored = true; }
            Check(lateOpenIgnored, "A late browser launch is ignored after cancellation");
            lateResult.SetResult(new("LateAuthor", null));
            await closingTask;
            await Idle(window);
            Check(!closing.IsVisible && !closing.IsBusy && closing.Draft.Author == snapshot.Author,
                "Late login completion cannot authenticate a cancelled dialog and deferred close completes");

            var profile = Path.Combine(Path.GetFullPath(output), "publish-profile 中文");
            Directory.CreateDirectory(profile);
            var store = new MarketplaceCredentialStore(profile);
            const string syntheticToken = "synthetic-marketplace-token-ONLY-FOR-LOCAL-CHECK";
            store.SaveToken(syntheticToken);
            Check(store.ReadToken() == syntheticToken && Path.GetDirectoryName(store.CredentialPath) == Path.Combine(profile, "Data")
                && !Encoding.UTF8.GetString(File.ReadAllBytes(store.CredentialPath)).Contains(syntheticToken, StringComparison.Ordinal),
                "Marketplace token round-trips through current-user DPAPI in isolated profile Data without plaintext token storage");
            Check(!new DiagnosticRedactor().Text("credential fixture: " + syntheticToken).Contains(syntheticToken, StringComparison.Ordinal),
                "Saved and loaded marketplace credentials register with diagnostic redaction");
            store.DeleteToken();
            Check(store.ReadToken() is null && !File.Exists(store.CredentialPath), "Logout removes the isolated marketplace credential file");

            var drafts = new MarketplaceDraftStore(profile);
            var firstKey = Guid.NewGuid().ToString("N");
            var secondKey = Guid.NewGuid().ToString("N");
            drafts.Save(firstKey, snapshot with { Title = "", WorkshopCollectionId = "未完成链接" }, false);
            drafts.Save(secondKey, snapshot with { Title = "更新草稿" }, true);
            drafts.Save(firstKey, snapshot with { Title = "修改后的草稿", Version = "未完成版本", WorkshopCollectionId = "未完成链接" }, false);
            var reopened = new MarketplaceDraftStore(profile).ReadAll();
            Check(reopened.Count == 2 && reopened.Single(item => item.Key == firstKey).Manifest.Version == "未完成版本"
                && reopened.Single(item => item.Key == secondKey).IsUpdate,
                "Independent draft keys retain multiple incomplete snapshots and replace only the edited draft across restarts");
            File.WriteAllText(Path.Combine(profile, "Data", "Marketplace", "Drafts", Guid.NewGuid().ToString("N") + ".json"), "invalid JSON");
            Check(drafts.ReadAll().Count == 2, "A malformed local draft cannot hide other drafts and remains untouched");
            store.SaveDraft(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            var legacy = drafts.ReadAll().Single(item => item.Key == "00000000000000000000000000000001");
            drafts.Save(legacy.Key, legacy.Manifest with { Title = "迁移草稿已编辑" }, false);
            Check(drafts.ReadAll().Count == 3 && drafts.ReadAll().Single(item => item.Key == legacy.Key).Title == "迁移草稿已编辑"
                && File.Exists(store.DraftPath), "Legacy single draft remains recoverable and edits reuse its deterministic storage key");

            var exact = reopened.Single(item => item.Key == secondKey);
            var productionEditor = Show(MarketplacePublishWindow.CreateProductionActions(exact.Manifest, profile, _ => { },
                isUpdate: exact.IsUpdate, savedDraft: exact), exact.Manifest);
            productionEditor.TitleBox.Text = "从管理窗口继续编辑";
            await productionEditor.SaveLocalDraftAsync();
            productionEditor.Close();
            Check(drafts.ReadAll().Count == 3 && drafts.ReadAll().Single(item => item.Key == exact.Key).Title == "从管理窗口继续编辑"
                && drafts.ReadAll().Single(item => item.Key == firstKey).Title == "修改后的草稿",
                "Resuming an exact saved draft uses its own snapshot, update mode and key without overwriting another draft");
            var manager = new MarketplaceMyOrdersWindow([], drafts.ReadAll()) { Owner = window };
            manager.Show();
            await Idle(manager);
            manager.DraftsList.SelectedIndex = 0;
            Check(manager.Title == "我的排序与草稿" && manager.DraftsList.Items.Count == 3 && manager.EditButton.IsEnabled
                && manager.SelectedDraft is not null,
                "Signed-out author can browse local drafts and select continue editing independently of approved orders");
            PrototypeSmokeCheck.SavePreview(manager, Path.Combine(output, "my-orders-and-drafts.png"));
            manager.Close();
            store.SaveToken(syntheticToken);
            drafts.Delete(legacy.Key);
            Check(!drafts.ReadAll().Any(item => item.Key == legacy.Key) && !File.Exists(store.DraftPath)
                && drafts.ReadAll().Count == 2 && store.ReadToken() == syntheticToken,
                "Deleting a migrated legacy draft removes its source without resurrection or touching credentials and other drafts");
            var removeCalls = 0;
            var deletionEntry = MarketplaceJson.CreateCatalogue([snapshot with { Author = "FixtureAuthor" }]).Entries.Single();
            var confirmed = true;
            var deletionReply = new TaskCompletionSource<MarketplaceSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
            var removeManager = new MarketplaceMyOrdersWindow([deletionEntry], drafts.ReadAll(), new MarketplaceMyOrdersActions
            {
                ConfirmDelete = _ => confirmed,
                DeleteDraft = item => drafts.Delete(item.Key),
                DeleteOrderAsync = (_, _, _) => { removeCalls++; return deletionReply.Task; }
            }) { Owner = window };
            removeManager.Show();
            await Idle(removeManager);
            removeManager.DraftsList.SelectedItem = removeManager.DraftsList.Items.Cast<MarketplaceSavedDraft>().Single(item => item.Key == firstKey);
            await removeManager.DeleteSelectedAsync();
            Check(removeManager.DraftsList.Items.Count == 1 && drafts.ReadAll().Count == 1 && removeCalls == 0,
                "Deleting a selected local draft refreshes only that list and never sends a remote request");
            removeManager.OrdersList.SelectedIndex = 0;
            confirmed = false;
            await removeManager.DeleteSelectedAsync();
            Check(removeCalls == 0, "Cancelling deletion confirmation performs no remote write");
            confirmed = true;
            var removal = removeManager.DeleteSelectedAsync();
            Check(removeManager.IsBusy && !removeManager.EditButton.IsEnabled, "Pending remote deletion disables editing and duplicate actions");
            await removeManager.DeleteSelectedAsync();
            removeManager.Close();
            Check(removeManager.IsVisible && removeCalls == 1, "A pending deletion cannot be duplicated or hidden by closing the window");
            deletionReply.SetResult(new(14, new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/14"), "fixture", deletionEntry.ManifestPath, deletionEntry.Sha256));
            await removal;
            await removeManager.DeleteSelectedAsync();
            Check(removeCalls == 1 && removeManager.OrdersList.Items.Count == 1 && removeManager.EmptyText.Text.Contains("核实原发布者后自动下架"),
                "A submitted removal reports automatic author verification without claiming the remote catalogue has already changed");
            Check(removeManager.OrdersList.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => item.Header.ToString()).SequenceEqual(["修改排序", "删除"])
                && removeManager.DraftsList.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => item.Header.ToString()).SequenceEqual(["修改排序", "删除"])
                && removeManager.EditButton.Content.ToString() == "修改排序" && removeManager.FindName("ContinueButton") is null,
                "Both lists offer the same edit and delete context menu and one shared edit button");
            removeManager.Close();
            var olderEntry = deletionEntry with { Version = "0.9.0", ManifestPath = MarketplaceJson.GetManifestPath(deletionEntry.Author, deletionEntry.Id, "0.9.0") };
            var rangeDialog = new MarketplaceDeleteWindow([olderEntry, deletionEntry]) { Owner = window };
            rangeDialog.Show();
            await Idle(rangeDialog);
            Check(rangeDialog.SingleVersion.IsChecked == true && rangeDialog.VersionPicker.IsEnabled
                && ((MarketplaceEntry)rangeDialog.VersionPicker.SelectedItem).Version == deletionEntry.Version,
                "Deletion range dialog defaults to one explicit latest version rather than all versions");
            rangeDialog.AllVersions.IsChecked = true;
            Check(!rangeDialog.VersionPicker.IsEnabled, "All-version removal disables the single-version picker");
            rangeDialog.SingleVersion.IsChecked = true;
            rangeDialog.VersionPicker.SelectedIndex = 1;
            Check(((MarketplaceEntry)rangeDialog.VersionPicker.SelectedItem).Version == olderEntry.Version && rangeDialog.VersionPicker.IsEnabled,
                "Author can select an older version independently of the grouped market row");
            rangeDialog.Close();
            foreach (var choice in new MarketplaceDeletionScope?[] { null, MarketplaceDeletionScope.SelectedVersion, MarketplaceDeletionScope.AllVersions })
            {
                MarketplaceDeletionRequest? sent = null;
                var selectorCalls = 0;
                var grouped = new MarketplaceMyOrdersWindow([olderEntry, deletionEntry], [], new()
                {
                    ChooseDeletion = versions => { selectorCalls++; Check(versions.Count == 2, "Deletion receives every published version");
                        return choice is null ? null : new(olderEntry, choice.Value); },
                    DeleteOrderAsync = (entry, scope, _) => { sent = new(entry, scope); return Task.FromResult(new MarketplaceSubmission(15,
                        new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/15"), "synthetic", entry.ManifestPath, entry.Sha256)); }
                }) { Owner = window };
                grouped.Show();
                await Idle(grouped);
                grouped.OrdersList.SelectedIndex = 0;
                await grouped.DeleteSelectedAsync();
                Check(selectorCalls == 1 && (choice is null ? sent is null : sent?.Entry == olderEntry && sent.Scope == choice),
                    "Selected deletion range is passed exactly, cancellation sends no request: " + choice);
                grouped.Close();
            }
            foreach (var editDraft in new[] { true, false })
            {
                var doubleClick = new MarketplaceMyOrdersWindow([deletionEntry], drafts.ReadAll()) { Owner = window };
                doubleClick.ContentRendered += (_, _) => doubleClick.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var list = editDraft ? doubleClick.DraftsList : doubleClick.OrdersList;
                    var item = (System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    var right = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = item };
                    list.RaiseEvent(right);
                    Check(list.SelectedIndex == 0, "Right-click selects the pointed row before context actions");
                    var blank = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
                    list.RaiseEvent(blank);
                    Check(list.SelectedIndex == -1 && blank.Handled, "Right-clicking empty list space cannot target the previous row");
                    var twice = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                        System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = item };
                    list.RaiseEvent(twice);
                    // A failed handler must not leave a modal smoke check open.
                    if (doubleClick.IsVisible) doubleClick.Close();
                }));
                var result = doubleClick.ShowDialog();
                Check(result == true && (editDraft ? doubleClick.SelectedDraft is not null : doubleClick.SelectedEntry is not null),
                    "Double-click opens the selected draft or published order through the unified modification action");
            }
            using (var updateClient = new MarketplaceClient(handler: new ManifestHandler(snapshot with { Author = "FixtureAuthor" })))
            {
                var update = await MainWindow.LoadMarketplaceUpdateAsync(deletionEntry, updateClient, CancellationToken.None);
                Check(update.Mods.SequenceEqual(snapshot.Mods) && update.Id == snapshot.Id && update.Version == "1.1"
                    && update.ReleaseNotes.Length == 0, "Published modification loads its validated original snapshot and a new version with optional empty notes");
            }
            store.DeleteToken();
        }
        finally
        {
            foreach (var dialog in dialogs)
            {
                dialog.Cancel();
                if (dialog.IsVisible) dialog.Close();
            }
        }
    }

    private static async Task CheckFilePublishingAsync(MainWindow owner, string output, List<string> checks)
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        var main = new MainWindow(initializePaths: false);
        try
        {
            main.LoadSample();
            main.ViewModel.SelectedRow = main.ViewModel.Rows.Last();
            main.ViewModel.MoveUpCommand.Execute(null);
            main.ViewModel.MoveUpCommand.Execute(null);
            main.ViewModel.UndoCommand.Execute(null);
            main.ViewModel.SearchText = "保留搜索";
            main.ViewModel.IsScanning = main.ViewModel.IsShowingCache = true;
            var codec = new OrderFileCodec();
            var before = codec.Write(main.ViewModel.CreateOrderFile(), OrderFileFormat.ModsConfigXml);
            var revision = main.ViewModel.ContentRevision;
            var source = main.ViewModel.SourceLabel;
            var dirty = main.ViewModel.HasChanges;
            var undo = main.ViewModel.UndoCommand.CanExecute(null);
            var redo = main.ViewModel.RedoCommand.CanExecute(null);
            Check(dirty && undo && redo, "文件发布隔离验收使用有未保存修改且包含撤销/取消撤销历史的当前排序。");
            var path = Path.Combine(output, "其他排序文件.rml");
            var xml = "<savedModList><meta><gameVersion>1.6.4633</gameVersion>"
                + "<modIds><li>ludeon.rimworld</li><li>example.z</li><li>ludeon.rimworld.biotech</li><li>example.a</li></modIds>"
                + "<modSteamIds><li>0</li><li>123456</li><li>0</li><li>234567</li></modSteamIds></meta>"
                + "<modList><ids><li>ludeon.rimworld</li><li>example.z</li><li>ludeon.rimworld.biotech</li><li>example.a</li></ids></modList></savedModList>";
            await File.WriteAllTextAsync(path, xml, Encoding.UTF8);
            var original = await File.ReadAllBytesAsync(path);
            bool Unchanged() => codec.Write(main.ViewModel.CreateOrderFile(), OrderFileFormat.ModsConfigXml).SequenceEqual(before)
                && main.ViewModel.ContentRevision == revision && main.ViewModel.SourceLabel == source
                && main.ViewModel.HasChanges == dirty && main.ViewModel.SearchText == "保留搜索"
                && main.ViewModel.UndoCommand.CanExecute(null) == undo && main.ViewModel.RedoCommand.CanExecute(null) == redo;
            var draft = await main.LoadMarketplaceFileDraftAsync(path, CancellationToken.None);
            Check(draft.Mods.Select(mod => mod.PackageId).SequenceEqual(new[]
                { "ludeon.rimworld", "example.z", "ludeon.rimworld.biotech", "example.a" })
                && draft.Mods[1].WorkshopId == "123456" && draft.Mods[3].WorkshopId == "234567"
                && draft.GameVersion == "1.6" && draft.RequiredDlc.SequenceEqual(["ludeon.rimworld.biotech"])
                && draft.Title == "其他排序文件" && draft.Version == "1.0",
                "其他排序文件独立读取原顺序、工坊编号、DLC及自身游戏版本，不套用当前游戏版本或当前清单。");
            Check(Unchanged() && (await File.ReadAllBytesAsync(path)).SequenceEqual(original),
                "读取其他排序文件不导入、不排序、不保存游戏配置，不改变当前清单、来源、搜索、修改状态及撤销历史。");
            var saves = 0;
            MarketplaceManifest? submitted = null;
            MarketplacePublishWindow Editor() => new(draft, new MarketplacePublishActions
            {
                InitialUser = new("FixtureAuthor", null), SnapshotLabel = "文件排序快照 · 其他排序文件.rml（不会修改当前排序）",
                LoginAsync = (_, _, _) => throw new InvalidOperationException("Unexpected login"),
                SubmitAsync = (manifest, _, _) =>
                {
                    submitted = manifest;
                    return Task.FromResult(new MarketplaceSubmission(17,
                        new Uri("https://github.com/Manhattanca/RimWise-Marketplace/pull/17"), "fixture", "fixture.json", new string('a', 64)));
                },
                SaveDraft = _ => saves++, Logout = () => { }, OpenTrustedLink = _ => { }
            }) { Owner = owner };
            var cancelled = Editor();
            cancelled.Show();
            cancelled.DescriptionBox.Text = "关闭后不提交";
            cancelled.Close();
            Check(saves == 0 && submitted is null && Unchanged(), "关闭其他文件发布编辑器不会自动保存草稿或替换当前排序。");
            var editor = Editor();
            try
            {
                editor.Show();
                Check(editor.SnapshotSummary.Text.Contains("其他排序文件.rml") && editor.SnapshotSummary.Text.Contains("不会修改当前排序"),
                    "文件读取成功后进入简介编辑器，明确显示独立文件快照来源。");
                await Idle(editor);
                PrototypeSmokeCheck.SavePreview(editor, Path.Combine(output, "market-publish-other-file.png"));
                editor.DescriptionBox.Text = "其他文件的发布简介";
                await editor.SubmitAsync();
                Check(submitted is not null && submitted.Mods.SequenceEqual(draft.Mods)
                    && submitted.Description == "其他文件的发布简介" && Unchanged() && saves == 0,
                    "其他文件投稿发送所选文件快照和编辑后的简介，不发送当前清单、不自动保存草稿。");
            }
            finally { editor.Close(); }
            foreach (var format in new[] { OrderFileFormat.ModsConfigXml, OrderFileFormat.RimSortJson, OrderFileFormat.ModListXml })
            {
                var another = Path.Combine(output, "publish-other-" + format + ".data");
                await File.WriteAllBytesAsync(another, codec.Write(new OrderFileService().Read(path), format));
                var converted = await main.LoadMarketplaceFileDraftAsync(another, CancellationToken.None);
                Check(converted.Mods.Select(mod => mod.PackageId).SequenceEqual(draft.Mods.Select(mod => mod.PackageId)) && Unchanged(),
                    $"其他文件发布按内容识别 {format}，不依赖扩展名，不导入当前清单。");
            }
            var unknownVersion = Path.Combine(output, "publish-no-version.xml");
            await File.WriteAllTextAsync(unknownVersion, "<ModList><modIds><li>ludeon.rimworld</li><li>example.a</li></modIds></ModList>");
            Check((await main.LoadMarketplaceFileDraftAsync(unknownVersion, CancellationToken.None)).GameVersion == "" && Unchanged(),
                "其他文件未声明游戏版本时留给发布者选择，不冒用当前排序版本。");
            foreach (var invalid in new[] { "MZ harmless non-image fixture", "<ModsConfigData><activeMods>",
                "<!DOCTYPE x [<!ENTITY value SYSTEM 'file:///not-read'>]><ModsConfigData><activeMods>&value;</activeMods></ModsConfigData>",
                "<ModsConfigData><activeMods/></ModsConfigData>" })
            {
                var bad = Path.Combine(output, "publish-invalid.data");
                await File.WriteAllTextAsync(bad, invalid);
                var rejected = false;
                try { await main.LoadMarketplaceFileDraftAsync(bad, CancellationToken.None); }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or ArgumentException) { rejected = true; }
                Check(rejected && Unchanged(), "非排序、损坏、DTD或空清单拒绝进入文件发布编辑，不影响当前排序。");
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelledRead = false;
            try { await main.LoadMarketplaceFileDraftAsync(path, cancellation.Token); }
            catch (OperationCanceledException) { cancelledRead = true; }
            Check(cancelledRead && Unchanged() && (await File.ReadAllBytesAsync(path)).SequenceEqual(original),
                "取消文件读取不更改所选原文件或当前排序。");
            var empty = new MainWindow(initializePaths: false);
            try
            {
                var standalone = await empty.LoadMarketplaceFileDraftAsync(path, CancellationToken.None);
                Check(!empty.ViewModel.HasDocument && standalone.Mods.SequenceEqual(draft.Mods),
                    "没有载入当前排序时也可发布其他排序文件，无需先导入或刷新。");
            }
            finally { empty.Close(); }
        }
        finally { main.ViewModel.MarkExported(); main.Close(); }
    }

    private sealed class ManifestHandler(MarketplaceManifest manifest) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { RequestMessage = request, Content = new System.Net.Http.ByteArrayContent(MarketplaceJson.SerializeManifest(manifest)) });
        }
    }

    private static async Task Idle(Window window)
        => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
}
#endif
