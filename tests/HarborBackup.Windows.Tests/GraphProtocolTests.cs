using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class GraphProtocolTests
{
    [TestMethod]
    public async Task ImmutableMetadataTriesCreateBeforeReconcilingOnlyAnExplicitConflict()
    {
        var methods = new List<HttpMethod>();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            methods.Add(request.Method);
            return Task.FromResult(request.Method == HttpMethod.Put
                ? Json(new { error = new { code = "nameAlreadyExists" } }, HttpStatusCode.Conflict)
                : Json(new { id = "existing", name = "record.json", size = 3, eTag = "etag" }));
        }));
        using var source = new MemoryStream([1, 2, 3]);
        var item = await drive.UploadImmutableAsync("parent", "record.json", source, default);
        Assert.AreEqual("existing", item.Id);
        CollectionAssert.AreEqual(new[] { HttpMethod.Put, HttpMethod.Get }, methods);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task CatalogSkipsDeletedTargetsButReportsAccessFailuresWithoutCloudWrites(HttpStatusCode failure)
    {
        using var ws = new TestWorkspace();
        var data = new HarborBackup.Windows.Integration.DataPaths(Path.Combine(ws.Root, "catalog-data"));
        var account = new HarborBackup.Windows.Persistence.Configuration.CloudAccountSettings(Enabled: true,
            DriveId: "drive", RootId: "management", RepositoryId: Guid.NewGuid().ToString("N"));
        var key = HarborBackup.Windows.Integration.DeviceIdentity.Digest("project");
        var mapping = HarborBackup.Windows.Integration.DeviceIdentity.Digest("mapping");
        var identity = JsonSerializer.SerializeToUtf8Bytes(new HarborBackup.Windows.Integration.CloudRepository.RepositoryIdentity(2, account.RepositoryId!, new()), HarborBackup.Windows.Persistence.AtomicJson.Options);
        var registration = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 3, projectId = key, driveId = "drive", folderId = "deleted", destination = "Projects/Demo", mappingId = mapping, generation = 1 });
        object Folder(string id) => new { id, name = id, eTag = "etag", size = 0, folder = new { } };
        object Metadata(string id, long length) => new { id, name = "record.json", eTag = "etag", size = length, file = new { } };
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method, "Listing must not modify cloud registrations or files.");
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var suffix = path[(path.IndexOf("/drives/drive", StringComparison.Ordinal) + "/drives/drive".Length)..];
            var response = suffix switch
            {
                "/items/management:/repository.json" => Json(Metadata("identity", identity.Length)),
                "/items/identity" => Json(Metadata("identity", identity.Length)),
                "/items/identity/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(identity) },
                "/items/management" => Json(Folder("management")),
                "/items/management:/metadata" => Json(Folder("metadata")),
                "/items/metadata:/projects" => Json(Folder("projects")),
                "/items/projects/children" => Json(new { value = new[] { Folder(key) } }),
                var p when p == "/items/projects:/" + key => Json(Folder(key)),
                var p when p == "/items/" + key + ":/locations" => Json(Folder("locations")),
                "/items/locations/children" => Json(new { value = new[] { new { id = "registration", name = mapping + ".json", eTag = "etag", size = registration.Length, file = new { } } } }),
                "/items/registration" => Json(Metadata("registration", registration.Length)),
                "/items/registration/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(registration) },
                "/root" => Json(Folder("drive-root")),
                "/items/deleted" => Json(new { error = new { code = "synthetic-target-failure" } }, failure),
                _ => throw new AssertFailedException("Unexpected request: " + suffix)
            };
            return Task.FromResult(response);
        }));
        if (failure == HttpStatusCode.NotFound)
            Assert.IsEmpty(await HarborBackup.Windows.Integration.CloudProjectCatalog.ReadAsync(data, account, drive, default));
        else
            await Assert.ThrowsExactlyAsync<CloudRequestException>(() => HarborBackup.Windows.Integration.CloudProjectCatalog.ReadAsync(data, account, drive, default));
    }

    [TestMethod]
    public async Task FreshFileAndMetadataUseThreeWritesWithoutLookups()
    {
        var root = Path.Combine(Path.GetTempPath(), "HarborFastProtocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var calls = new List<HttpMethod>();
        try
        {
            using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async request =>
            {
                calls.Add(request.Method);
                Assert.AreNotEqual(HttpMethod.Get, request.Method);
                if (request.Method == HttpMethod.Patch)
                    return Json(new { id = "body", name = "a.txt", eTag = "dated", size = 3,
                        parentReference = new { id = "parent" }, file = new { hashes = new { quickXorHash = "unused" } } });
                Assert.AreEqual(HttpMethod.Put, request.Method);
                Assert.Contains("conflictBehavior=fail", request.RequestUri!.Query);
                var bytes = await request.Content!.ReadAsByteArrayAsync();
                using var input = new MemoryStream(bytes);
                var checksum = await QuickXor.ComputeAsync(input, default);
                var metadata = request.RequestUri.AbsolutePath.Contains("metadata", StringComparison.Ordinal);
                return Json(new { id = metadata ? "metadata-id" : "body", name = metadata ? "record.json" : "a.txt",
                    eTag = "created", size = bytes.Length, parentReference = new { id = metadata ? "metadata" : "parent" },
                    file = new { hashes = new { quickXorHash = checksum } } });
            }));
            using var source = new MemoryStream([1, 2, 3]);
            var uploaded = await drive.UploadAsync("parent", "a.txt", source, null, _ => { }, default, reconcileExisting: false);
            var dated = await drive.SetModifiedAsync(uploaded, DateTimeOffset.UtcNow, default);
            var repository = new HarborBackup.Windows.Integration.CloudRepository(drive,
                new HarborBackup.Windows.Persistence.Configuration.CloudAccountSettings(DriveId: "drive"),
                new HarborBackup.Windows.Integration.DataPaths(root));
            await repository.JsonAsync("metadata", "record.json", new { item = dated.Id }, default, reconcileExisting: false);
            CollectionAssert.AreEqual(new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Put }, calls);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task FreshUploadCollisionStopsWithoutLookupOrReplacementRetry()
    {
        var calls = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.Contains("conflictBehavior=fail", request.RequestUri!.Query);
            return Task.FromResult(Json(new { error = new { code = "nameAlreadyExists" } }, HttpStatusCode.Conflict));
        }));
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsExactlyAsync<CloudRequestException>(() =>
            drive.UploadAsync("parent", "file", source, null, _ => { }, default, reconcileExisting: false));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task UploadedFileTimestampUpdateChecksExpectedVersion()
    {
        var modified = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async request =>
        {
            Assert.AreEqual(HttpMethod.Patch, request.Method);
            Assert.AreEqual("expected", request.Headers.GetValues("If-Match").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual(modified, body.RootElement.GetProperty("fileSystemInfo").GetProperty("lastModifiedDateTime").GetDateTimeOffset());
            return Json(new { id = "id", name = "file", eTag = "updated", size = 3, parentReference = new { id = "parent" } });
        }));
        var result = await drive.SetModifiedAsync(new("id", "file", "expected", 3, false, ParentId: "parent"), modified, default);
        Assert.AreEqual("updated", result.ETag);
    }

    [TestMethod]
    public async Task OrdinaryMoveUsesExpectedVersionAndNeverReplacesAConflict()
    {
        var calls=0;
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(async request=>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Patch,request.Method);
            Assert.AreEqual("expected",request.Headers.GetValues("If-Match").Single());
            Assert.Contains("conflictBehavior=fail",request.RequestUri!.Query);
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("target",body.RootElement.GetProperty("parentReference").GetProperty("id").GetString());
            return Json(new{error=new{code="nameAlreadyExists"}},HttpStatusCode.Conflict);
        }));
        var error=await Assert.ThrowsExactlyAsync<CloudRequestException>(()=>drive.MoveAsync(new("id","file","expected",3,false),"target","file",null,default));
        Assert.AreEqual(HttpStatusCode.Conflict,error.StatusCode);Assert.AreEqual(1,calls);
    }
    [TestMethod]
    public async Task HistoryCleanupUsesConditionalDelete()
    {
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(request=>
        {
            Assert.AreEqual(HttpMethod.Delete,request.Method);
            Assert.AreEqual("expected",request.Headers.GetValues("If-Match").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed){Content=new StringContent("{}")});
        }));
        await Assert.ThrowsExactlyAsync<CloudRequestException>(()=>drive.DeleteAsync(new("id","archive","expected",0,true),default));
    }

    private sealed class Tokens : IAccessTokens { public Task<string> GetAsync(CancellationToken token)=>Task.FromResult("test-bearer"); }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request); }
    private static HttpResponseMessage Json(object value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(JsonSerializer.Serialize(value))};
    private static object Item(string id="id",string name="file")=>new{id,name,eTag="etag",size=3,file=new{hashes=new{quickXorHash="hash"}}};

    [TestMethod]
    public async Task DownloadNeverForwardsBearerToPreauthorizedUrl()
    {
        var graphCalls=0;var blobCalls=0;
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(request=>
        {
            if(request.RequestUri!.Host=="graph.microsoft.com")
            {
                graphCalls++;Assert.AreEqual("test-bearer",request.Headers.Authorization?.Parameter);
                if(request.RequestUri.AbsolutePath.EndsWith("/content"))
                {var response=new HttpResponseMessage(HttpStatusCode.Redirect);response.Headers.Location=new("https://test.1drv.com/download?secret=redacted");return Task.FromResult(response);}
                return Task.FromResult(Json(Item()));
            }
            blobCalls++;Assert.IsNull(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3])});
        }));
        using var output=new MemoryStream();
        await drive.DownloadAsync(new("id","file","etag",3,false),output,default);
        CollectionAssert.AreEqual(new byte[]{1,2,3},output.ToArray());Assert.AreEqual(1,blobCalls);Assert.AreEqual(3,graphCalls);
    }
    [TestMethod]
    [DataRow(HttpStatusCode.MovedPermanently)]
    [DataRow(HttpStatusCode.Redirect)]
    [DataRow(HttpStatusCode.SeeOther)]
    [DataRow(HttpStatusCode.TemporaryRedirect)]
    [DataRow(HttpStatusCode.PermanentRedirect)]
    public async Task DownloadFollowsMultipleRedirectsAndPreservesRangeWithoutBearer(HttpStatusCode redirectStatus)
    {
        var blobs = 0; var metadata = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            HttpResponseMessage Redirect(string location, HttpStatusCode status)
            {
                var result = new HttpResponseMessage(status);
                result.Headers.Location = new(location, UriKind.RelativeOrAbsolute);
                return result;
            }
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                Assert.IsNotNull(request.Headers.Authorization);
                if (request.RequestUri.AbsolutePath.EndsWith("/content"))
                    return Task.FromResult(Redirect("https://my.microsoftpersonalcontent.com/download", HttpStatusCode.Redirect));
                metadata++;
                return Task.FromResult(Json(Item()));
            }
            Assert.IsNull(request.Headers.Authorization);
            Assert.AreEqual(1L, request.Headers.Range?.Ranges.Single().From);
            blobs++;
            if (blobs == 1) return Task.FromResult(Redirect("https://test.1drv.com/step", redirectStatus));
            if (blobs == 2) return Task.FromResult(Redirect("/bytes", redirectStatus));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([2, 3]) };
            response.Content.Headers.ContentRange = new(1, 2, 3);
            return Task.FromResult(response);
        }));
        using var output = new MemoryStream(); output.WriteByte(1);
        await drive.DownloadAsync(new("id", "file", "etag", 3, false), output, default);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, output.ToArray());
        Assert.AreEqual(3, blobs); Assert.AreEqual(2, metadata);
    }

    [TestMethod]
    [DataRow("https://attacker.invalid/bytes")]
    [DataRow("http://test.1drv.com/bytes")]
    [DataRow("https://test.1drv.com:444/bytes")]
    [DataRow("https://user@test.1drv.com/bytes")]
    [DataRow("https://my.microsoftpersonalcontent.com.attacker.invalid/bytes")]
    [DataRow("cycle")]
    [DataRow("limit")]
    [DataRow("missing")]
    public async Task DownloadRejectsUnsafeOrEndlessRedirectChainWithoutWriting(string target)
    {
        var blobs = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            var graph = request.RequestUri!.Host == "graph.microsoft.com";
            if (graph && !request.RequestUri.AbsolutePath.EndsWith("/content")) return Task.FromResult(Json(Item()));
            if (!graph) { blobs++; Assert.IsNull(request.Headers.Authorization); }
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            var location = graph ? "https://my.microsoftpersonalcontent.com/start" : target switch
            {
                "cycle" => "https://my.microsoftpersonalcontent.com/start",
                "limit" => "https://my.microsoftpersonalcontent.com/step" + blobs,
                "missing" => null,
                _ => target
            };
            if (location is not null) response.Headers.Location = new(location);
            return Task.FromResult(response);
        }));
        using var output = new MemoryStream(); output.WriteByte(1);
        await Assert.ThrowsExactlyAsync<IOException>(() => drive.DownloadAsync(new("id", "file", "etag", 3, false), output, default));
        CollectionAssert.AreEqual(new byte[] { 1 }, output.ToArray());
        Assert.AreEqual(target == "limit" ? 10 : 1, blobs);
    }
    [TestMethod]
    public async Task UntrustedPaginationUrlIsRejectedBeforeTokenIsSent()
    {
        var requests=0;
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(request=>
        {requests++;return Task.FromResult(Json(new Dictionary<string,object>{["value"]=Array.Empty<object>(),["@odata.nextLink"]="https://attacker.invalid/steal"}));}));
        await Assert.ThrowsExactlyAsync<IOException>(()=>drive.ChildrenAsync("root",default));Assert.AreEqual(1,requests);
    }
    [TestMethod]
    public async Task UploadResumesAtServerOffsetAndDoesNotForwardBearer()
    {
        const int offset=320*1024;var data=new byte[offset+3];data[^3]=1;data[^2]=2;data[^1]=3;
        var chunks=0;
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(async request=>
        {
            if(request.RequestUri!.Host=="graph.microsoft.com")
            {
                if(request.Method==HttpMethod.Patch) return Json(new { id="uploaded", name="file", eTag="published", size=data.Length });
                return new(HttpStatusCode.NotFound);
            }
            Assert.IsNull(request.Headers.Authorization);
            if(request.Method==HttpMethod.Get)return Json(new{nextExpectedRanges=new[]{offset+"-"}});
            chunks++;
            Assert.AreEqual((long)offset,request.Content!.Headers.ContentRange?.From);
            CollectionAssert.AreEqual(new byte[]{1,2,3},await request.Content.ReadAsByteArrayAsync());
            return Json(new{id="uploaded",name=".harbor-upload-0123456789abcdef0123456789abcdef.tmp",eTag="new",size=data.Length},HttpStatusCode.Created);
        }));
        using var input=new MemoryStream(data);var completed=false;
        var result=await drive.UploadAsync("parent","file",input,new("https://test.1drv.com/upload",DateTimeOffset.UtcNow.AddHours(1),0,".harbor-upload-0123456789abcdef0123456789abcdef.tmp"),
            checkpoint=>completed=checkpoint is null,default);
        Assert.AreEqual("uploaded",result.Id);Assert.IsTrue(completed);Assert.AreEqual(1,chunks);
    }
    [TestMethod]
    public async Task QuickXorMatchesIndependentBitVectorAcrossChunkBoundary()
    {
        var data=Enumerable.Range(0,131100).Select(i=>(byte)(i*19)).ToArray();
        var bits=new bool[160];
        for(var i=0;i<data.Length;i++)for(var b=0;b<8;b++)if((data[i]&(1<<b))!=0)bits[(i*11+b)%160]^=true;
        var expected=new byte[20];for(var i=0;i<bits.Length;i++)if(bits[i])expected[i/8]|=(byte)(1<<(i%8));
        var length=BitConverter.GetBytes((long)data.Length);for(var i=0;i<8;i++)expected[12+i]^=length[i];
        using var input=new MemoryStream(data);
        Assert.AreEqual(Convert.ToBase64String(expected),await QuickXor.ComputeAsync(input,default));
    }

    [TestMethod]
    public async Task RangeDownloadContinuesStagedFileWithoutDuplicatingPrefix()
    {
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(request=>
        {
            if(request.RequestUri!.Host=="graph.microsoft.com")
            {
                if(request.RequestUri.AbsolutePath.EndsWith("/content"))
                {var redirect=new HttpResponseMessage(HttpStatusCode.Redirect);redirect.Headers.Location=new("https://test.1drv.com/bytes");return Task.FromResult(redirect);}
                return Task.FromResult(Json(Item()));
            }
            Assert.AreEqual(1L,request.Headers.Range?.Ranges.Single().From);
            Assert.IsNull(request.Headers.Authorization);
            var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent([2,3])};
            response.Content.Headers.ContentRange=new(1,2,3);return Task.FromResult(response);
        }));
        using var output=new MemoryStream();output.WriteByte(1);
        await drive.DownloadAsync(new("id","file","etag",3,false),output,default);
        CollectionAssert.AreEqual(new byte[]{1,2,3},output.ToArray());
    }

    [TestMethod]
    public async Task ExpiredDeltaCursorRebuildsAndFollowsAllPages()
    {
        var calls=0;
        using var drive=new GraphDrive(new Tokens(),"drive",new Handler(request=>
        {
            calls++;
            if(request.RequestUri!.AbsolutePath.EndsWith("/expired"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
            if(request.RequestUri.AbsolutePath.EndsWith("/page2"))
                return Task.FromResult(Json(new Dictionary<string,object>{["value"]=Array.Empty<object>(),["@odata.deltaLink"]="https://graph.microsoft.com/v1.0/drives/drive/root/delta?token=new"}));
            return Task.FromResult(Json(new Dictionary<string,object>{["value"]=new[]{new{id="changed"}},["@odata.nextLink"]="https://graph.microsoft.com/v1.0/page2"}));
        }));
        var result=await drive.ChangesAsync("https://graph.microsoft.com/v1.0/expired",default);
        Assert.IsTrue(result.Changed);Assert.Contains("token=new",result.Cursor);Assert.AreEqual(3,calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public async Task SmallUploadsUseCreateOnlyContentEndpoint(int length)
    {
        var calls = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async request =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.Contains("conflictBehavior=fail", request.RequestUri!.Query);
            Assert.AreEqual("test-bearer", request.Headers.Authorization?.Parameter);
            Assert.HasCount(length, await request.Content!.ReadAsByteArrayAsync());
            return Json(new { id = "new", name = "file", eTag = "new-tag", size = length }, HttpStatusCode.Created);
        }));
        using var source = new MemoryStream(new byte[length]);
        var result = await drive.UploadAsync("parent", "file", source, null, _ => Assert.Fail("No session for small files."), default);
        Assert.AreEqual("new", result.Id); Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ExistingFileIsNeverWritten()
    {
        var calls = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            calls++; Assert.AreEqual(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(Item()));
        }));
        using var source = new MemoryStream([9, 9, 9]);
        var result = await drive.UploadAsync("parent", "file", source, null, _ => Assert.Fail(), default);
        Assert.AreEqual("id", result.Id); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Conflict)]
    [DataRow(HttpStatusCode.PreconditionFailed)]
    public async Task ConcurrentSmallFileCreationNeverRetriesWithReplace(HttpStatusCode status)
    {
        var writes = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            writes++;
            Assert.Contains("conflictBehavior=fail", request.RequestUri!.Query);
            return Task.FromResult(Json(new { error = new { code = "nameAlreadyExists" } }, status));
        }));
        using var source = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() =>
            drive.UploadAsync("parent", "file", source, null, _ => Assert.Fail(), default));
        Assert.AreEqual(status, error.StatusCode);
        Assert.AreEqual("Upload file content", error.Operation);
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    [DataRow(3276800, 2)]
    [DataRow(10485760, 1)]
    public async Task LargeUploadStagesBeforePublishingAndNeverSendsBearerToContentHost(int chunkBytes, int expectedChunks)
    {
        const int length = 4 * 1024 * 1024 + 1;
        TransferSession? saved = null;
        string? stage = null;
        var chunks = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async request =>
        {
            if (request.RequestUri!.Host == "my.microsoftpersonalcontent.com")
            {
                Assert.IsNull(request.Headers.Authorization);
                Assert.AreEqual(HttpMethod.Put, request.Method);
                chunks++;
                var range = request.Content!.Headers.ContentRange!;
                if (range.To != length - 1)
                    return Json(new { nextExpectedRanges = new[] { (range.To + 1) + "-" }, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) }, HttpStatusCode.Accepted);
                return Json(new { id = "staged", name = stage, eTag = "stage-tag", size = length }, HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            Assert.AreEqual("test-bearer", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Post)
            {
                Assert.IsNotNull(saved?.StagingName, "Journal must reserve staging identity before network mutation.");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                stage = json.RootElement.GetProperty("item").GetProperty("name").GetString();
                Assert.AreEqual(saved.StagingName, stage);
                Assert.IsFalse(json.RootElement.GetProperty("item").TryGetProperty("@microsoft.graph.conflictBehavior", out _));
                Assert.Contains(stage!, request.RequestUri.AbsolutePath);
                return Json(new { uploadUrl = "https://my.microsoftpersonalcontent.com/upload?secret=hidden", expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) });
            }
            Assert.AreEqual(HttpMethod.Patch, request.Method);
            Assert.AreEqual("staged", saved?.CompletedItemId);
            Assert.AreEqual("stage-tag", request.Headers.GetValues("If-Match").Single());
            Assert.Contains("conflictBehavior=fail", request.RequestUri.Query);
            return Json(new { id = "staged", name = "file", eTag = "published", size = length });
        }), new(chunkBytes));
        using var source = new MemoryStream(new byte[length]);
        var result = await drive.UploadAsync("parent", "file", source, null, value => saved = value, default);
        Assert.AreEqual("file", result.Name); Assert.IsNull(saved); Assert.AreEqual(expectedChunks, chunks);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletedUploadRecoversWithoutRetransferAndRetainsEvidenceOnConflict(bool conflict)
    {
        const string stage = ".harbor-upload-0123456789abcdef0123456789abcdef.tmp";
        TransferSession? saved = new("", DateTimeOffset.MinValue, 0, stage, "stage-id", "stage-tag");
        var writes = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            Assert.AreEqual("graph.microsoft.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/stage-id", StringComparison.Ordinal)
                    ? Json(new { id = "stage-id", name = stage, eTag = "stage-tag", size = 3 })
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            writes++; Assert.AreEqual(HttpMethod.Patch, request.Method);
            Assert.Contains("conflictBehavior=fail", request.RequestUri.Query);
            return Task.FromResult(conflict
                ? Json(new { error = new { code = "nameAlreadyExists" } }, HttpStatusCode.Conflict)
                : Json(new { id = "stage-id", name = "file", eTag = "published", size = 3 }));
        }));
        using var source = new MemoryStream([1, 2, 3]);
        if (conflict)
        {
            var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() =>
                drive.UploadAsync("parent", "file", source, saved, value => saved = value, default));
            Assert.AreEqual("Publish uploaded file", error.Operation);
            Assert.AreEqual("stage-id", saved?.CompletedItemId);
        }
        else
        {
            await drive.UploadAsync("parent", "file", source, saved, value => saved = value, default);
            Assert.IsNull(saved);
        }
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    [DataRow("https://my.microsoftpersonalcontent.com.attacker.invalid/upload")]
    [DataRow("https://attacker.microsoftpersonalcontent.com/upload")]
    [DataRow("http://my.microsoftpersonalcontent.com/upload")]
    [DataRow("https://my.microsoftpersonalcontent.com:8443/upload")]
    public async Task UntrustedUploadEndpointsAreRejectedBeforeNetworkAccess(string url)
    {
        var calls = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            calls++; Assert.AreEqual("graph.microsoft.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsExactlyAsync<IOException>(() => drive.UploadAsync("parent", "file", source,
            new(url, DateTimeOffset.UtcNow.AddHours(1), 0, ".harbor-upload-0123456789abcdef0123456789abcdef.tmp"), _ => { }, default));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ErrorsExposeStructuredDiagnosticsWithoutAuthorizationUrlsOrRawBody()
    {
        const string id = "01234567-89ab-cdef-0123-456789abcdef";
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => Task.FromResult(Json(
            new { error = new { code = "invalidRequest", message = "Bearer secret-token https://my.microsoftpersonalcontent.com/upload?secret=hidden",
                innerError = new Dictionary<string, string> { ["request-id"] = id } } }, HttpStatusCode.BadRequest))));
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.AreEqual("invalidRequest", error.ErrorCode);
        Assert.AreEqual(id, error.RequestId);
        Assert.AreEqual("Read cloud item", error.Operation);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("https://", error.ToString());
        Assert.Contains("400", error.Message);
    }

    [TestMethod]
    [DataRow("<html>secret-token</html>")]
    [DataRow("{\"error\":{\"code\":\"https://attacker.invalid/token\",\"innerError\":{\"request-id\":\"Bearer secret\"}}}")]
    public async Task MalformedErrorsStillReportHttpStatusWithoutLeakingBody(string body)
    {
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) })));
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.AreEqual(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.AreEqual("unknown", error.ErrorCode); Assert.IsNull(error.RequestId);
        Assert.DoesNotContain("secret", error.ToString()); Assert.DoesNotContain("https://", error.ToString());
    }

}
