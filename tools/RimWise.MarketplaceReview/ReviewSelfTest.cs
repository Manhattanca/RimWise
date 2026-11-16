using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Marketplace;

namespace RimWise.MarketplaceReview;

internal static class ReviewSelfTest
{
    private static readonly string Head = new('a',40), Base = new('b',40), Merge = new('c',40);
    internal static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory); File.Delete(Path.Combine(directory,"failure.txt")); var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            // Render only this app's own isolated UI. Never read user credentials or enable startup here.
            using(var window=new ReviewWindow(false,selfTest:true))
            {
                using var sampleBitmap = new System.Drawing.Bitmap(240, 140);
                using (var g = System.Drawing.Graphics.FromImage(sampleBitmap)) { g.Clear(System.Drawing.Color.FromArgb(33,77,80)); g.DrawString("RimWise",new System.Drawing.Font("Segoe UI",22),System.Drawing.Brushes.White,45,45); }
                using var imageStream = new MemoryStream(); sampleBitmap.Save(imageStream,System.Drawing.Imaging.ImageFormat.Png);
                var image=MarketplaceImages.Prepare("Manhattanca","synthetic-review","1.0.0",0,imageStream.ToArray());
                window.PreviewForChecks(new(Pull(),"标题：合成排序审核示例\r\n发布者：Manhattanca\r\n游戏版本：1.7 / 1.6\r\n\r\n简介：\r\n这里展示作者填写的简介，以纯文本显示。\r\n\r\n更新日志：\r\n本示例只供界面验证，没有真实投稿。",[],[new(image.Image,image.Content)],true,"这是隔离测试示例，没有连接 GitHub 或修改真实 PR。"));
                window.Show();window.PerformLayout();using var bitmap=new System.Drawing.Bitmap(window.Width,window.Height);
                window.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,window.Width,window.Height));bitmap.Save(Path.Combine(directory,"review-window.png"));
                Check(window.Text.Contains("审核助手"),"Chinese themed independent review window creates without credentials or network");window.Hide();
            }
            SynchronizationContext.SetSynchronizationContext(null);
            var settings=new ReviewSettings{LastCheckUtc=DateTimeOffset.UtcNow};
            Check(settings.NextCheckUtc-settings.LastCheckUtc.Value==TimeSpan.FromMinutes(30),"Polling interval is exactly thirty minutes");
            Check(settings.NextCheckUtc>DateTimeOffset.UtcNow,"No background polling before next deadline");
            Check(ReviewSettings.StartupCommand().EndsWith("\" --background"),"Startup command quotes exact executable and uses background mode");
            var state=new NotificationState();var first=Pull();Check(state.Update([first]).Count==1,"New PR notifies once");Check(state.Update([first]).Count==0,"Unchanged PR does not notify again");Check(state.Update([first with{Sha=Merge}]).Count==1,"New PR head notifies again");
            foreach(var mode in new[]{"success","head-changed","base-changed","final-head-changed","final-base-changed","draft","closed","failed-check","queued-check","running-check","missing-check","fake-app","foreign-workflow","old-base-proof","merge-rejected","cancelled"})
            {
                var server=new Server(mode);using var api=new ReviewApi(server);api.SetToken("synthetic-test-token");
                var snapshot=new ReviewSnapshot(Pull(),"合成文本",[],[],true,"");using var cancel=new CancellationTokenSource();if(mode=="cancelled")cancel.Cancel();
                bool succeeded=false;try{await api.MergeAsync(snapshot,cancel.Token);succeeded=true;}catch(Exception ex)when(ex is InvalidOperationException or OperationCanceledException){if(mode=="success")throw new InvalidOperationException("Expected merge failed",ex);}
                Check(succeeded==(mode=="success"),"Merge gate: "+mode);
                Check(server.Writes==(mode is "success" or "merge-rejected"?1:0),"No unauthorized merge write: "+mode);
                if(mode=="success")Check(server.MergeSha==Head,"Merge request pins reviewed head SHA");
            }
            foreach(var pair in new[]{("success",""),("queued-check","正在等待或运行"),("running-check","正在等待或运行"),("failed-check","未通过或已取消"),("missing-check","尚未找到"),("old-base-proof","未证明当前主分支"),("fake-app","尚未找到"),("foreign-workflow","尚未找到")})
            {
                var server=new Server(pair.Item1);using var api=new ReviewApi(server);api.SetToken("synthetic-test-token");
                string message="";try{await api.CheckReadinessAsync(Pull(),CancellationToken.None);}catch(InvalidOperationException ex){message=ex.Message;}
                Check(pair.Item2.Length==0?message.Length==0:message.Contains(pair.Item2),"Readiness reports actual check state: "+pair.Item1);
                Check(server.Writes==0,"Readiness never merges or reruns workflows: "+pair.Item1);
            }
            foreach(var mode in new[]{"inspect","tampered-image","extra-file","wrong-author","removal","removal-other-user","removal-partial","removal-extra","removal-stale"})
            {
                var server=new Server(mode);using var api=new ReviewApi(server);api.SetToken("synthetic-test-token");
                bool accepted=false;try{var snapshot=await api.InspectAsync(7,CancellationToken.None);accepted=snapshot.Supported;if(accepted)Check(mode.StartsWith("removal")?snapshot.IsRemoval&&snapshot.Images.Count==0:snapshot.Images.Count==1,"Inspection reads the correct publication or removal scope");}catch(Exception ex)when(ex is MarketplaceException or InvalidDataException or InvalidOperationException){}
                Check(accepted==(mode is "inspect" or "removal" or "removal-partial"),"Pinned image or author removal inspection: "+mode);Check(server.Writes==0,"Inspect is read-only: "+mode);
            }
            using(var handler=new Server("inspect"))using(var api=new ReviewApi(handler))
            {api.SetToken("synthetic-test-token");var identity=await api.IdentityAsync(CancellationToken.None);Check(identity=="Manhattanca","Maintainer identity requires repository push permission");var pulls=await api.ListAsync(CancellationToken.None);Check(pulls.Count==1,"Poll reads open main-branch PR list only");}
            using(var server=new Server("removal"))using(var api=new ReviewApi(server))
            {
                api.SetToken("synthetic-test-token");var removal=await api.InspectAsync(7,CancellationToken.None);
                await api.MergeAsync(removal,CancellationToken.None);
                Check(server.Writes==1&&server.MergeSha==Head&&server.MergeTitle=="作者本人下架排序 #7",
                    "Reviewer can merge a verified author removal with the exact head and correct removal action");
            }
            await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(directory,"failure.txt"),ex.ToString());return 1;}
    }
    private static ReviewPull Pull()=>new(7,"合成排序","Manhattanca","",Head,ReviewApi.Repository,Base,"main",false,DateTimeOffset.UtcNow) {AuthorId=123};
    private sealed class Server(string mode):HttpMessageHandler
    {
        internal int Writes;internal string? MergeSha;internal string? MergeTitle;
        private int gets;
        private readonly byte[] image=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        private MarketplaceManifest Manifest()
        {
            var author=mode=="wrong-author"?"another":"Manhattanca";
            return new MarketplaceManifest(1,"synthetic-review","1.0.0","合成排序",author,"<script>只是纯文本</script>","1.7",[],null,[new("ludeon.rimworld",null)])
                {Images=[MarketplaceImages.Prepare(author,"synthetic-review","1.0.0",0,image).Image]};
        }
        private object PullJson()=>new{number=7,title="合成排序",body="",user=new{login="Manhattanca",id=123L},state=mode=="closed"?"closed":"open",draft=mode=="draft",updated_at=DateTimeOffset.UtcNow,
            head=new{sha=mode=="head-changed"||(mode is "final-head-changed" or "removal-stale")&&gets>1?Merge:Head,repo=new{full_name=ReviewApi.Repository}},@base=new{sha=mode=="base-changed"||mode=="final-base-changed"&&gets>1?Merge:Base,@ref="main",repo=new{full_name=ReviewApi.Repository}},merge_commit_sha=Merge};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();var uri=request.RequestUri!;object response;
            if(uri.Host=="raw.githubusercontent.com")
            {
                if(request.Headers.Authorization is not null)throw new InvalidOperationException("Credentials leaked to raw host");
                var bytes=uri.AbsolutePath.EndsWith(".json")?MarketplaceJson.SerializeManifest(Manifest()):image.ToArray();
                if(mode=="tampered-image"&&!uri.AbsolutePath.EndsWith(".json"))bytes[30]^=1;
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes),RequestMessage=request};
            }
            var path=uri.AbsolutePath;
            if(path=="/user")response=new{login="Manhattanca"};
            else if(path=="/repos/"+ReviewApi.Repository)response=new{permissions=new{push=true}};
            else if(path.EndsWith("/pulls/7")){gets++;response=PullJson();}
            else if(path.EndsWith("/pulls/1/files"))response=new[]{new{filename=MarketplaceJson.GetManifestPath("Manhattanca","synthetic-review","1.0.0"),status="added"}};
            else if(path.EndsWith("/commits"))response=new[]{new{sha=Merge}};
            else if(path.EndsWith("/commits/"+Merge+"/pulls"))response=new[]{new{number=1,merged_at="2026-11-01T00:00:00Z",user=new{id=mode=="removal-other-user"?456:123},@base=new{@ref="main",repo=new{full_name=ReviewApi.Repository}}}};
            else if(path.Contains("/git/trees/"))
            {
                var paths=new List<object>{new{path=MarketplaceJson.GetManifestPath("Manhattanca","synthetic-review","1.0.0"),type="blob"}};
                if(mode=="removal-partial")paths.Add(new{path=MarketplaceJson.GetManifestPath("Manhattanca","synthetic-review","1.0.1"),type="blob"});
                response=new{truncated=false,tree=paths};
            }
            else if(path.EndsWith("/files"))
            {
                var m=Manifest();var status=mode.StartsWith("removal")?"removed":"added";var paths=new List<object>{new{filename=MarketplaceJson.GetManifestPath(m.Author,m.Id,m.Version),status},new{filename=m.Images[0].Path,status}};
                if(mode is "extra-file" or "removal-extra")paths.Add(new{filename=".github/workflows/evil.yml",status});response=paths;
            }
            else if(path.EndsWith("/pulls"))response=new[]{PullJson()};
            else if(path.EndsWith("/check-runs")&&mode=="missing-check")response=new{total_count=0,check_runs=Array.Empty<object>()};
            else if(path.EndsWith("/check-runs"))response=new{total_count=1,check_runs=new[]{new{name="validate-submission",status=mode=="queued-check"?"queued":mode=="running-check"?"in_progress":"completed",conclusion=mode is "queued-check" or "running-check"?null:mode=="failed-check"?"failure":"success",head_sha=Head,
                app=new{id=mode=="fake-app"?1:15368,slug="github-actions"},check_suite=new{id=99L},details_url="https://github.com/Manhattanca/RimWise-Marketplace/actions/runs/123/job/456"}}};
            else if(path.EndsWith("/actions/runs/123"))response=new{path=mode=="foreign-workflow"?".github/workflows/foreign.yml":".github/workflows/marketplace.yml",@event="pull_request",head_sha=Head,check_suite_id=99L,repository=new{full_name=ReviewApi.Repository},pull_requests=Array.Empty<object>()};
            else if(path.EndsWith("/jobs"))response=new{total_count=1,jobs=new[]{new{name="validate-submission",conclusion="success",steps=new[]{new{name="Validate data with the trusted base-branch tool (base "+(mode=="old-base-proof"?Merge:Base)+")",status="completed",conclusion="success"}}}}};
            else if(path.EndsWith("/merge"))
            {Writes++;using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));MergeSha=body.RootElement.GetProperty("sha").GetString();MergeTitle=body.RootElement.GetProperty("commit_title").GetString();response=new{merged=mode!="merge-rejected"};}
            else throw new InvalidOperationException("Unexpected mock request "+uri);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response),Encoding.UTF8,"application/json"),RequestMessage=request};
        }
    }
}
