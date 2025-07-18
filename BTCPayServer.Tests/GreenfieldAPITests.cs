using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.NTag424;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.PayoutProcessors.Lightning;
using BTCPayServer.Plugins.PointOfSale.Controllers;
using BTCPayServer.Plugins.PointOfSale.Models;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Mails;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Notifications.Blobs;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitpayClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using CreateApplicationUserRequest = BTCPayServer.Client.Models.CreateApplicationUserRequest;
using PosViewType = BTCPayServer.Plugins.PointOfSale.PosViewType;

namespace BTCPayServer.Tests
{
    [Collection(nameof(NonParallelizableCollectionDefinition))]
    public class GreenfieldAPITests : UnitTestBase
    {
        public const int TestTimeout = TestUtils.TestTimeout;
        public GreenfieldAPITests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        [Trait("Lightning", "Lightning")]
        public async Task LocalClientTests()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var user = tester.NewAccount();
            await user.GrantAccessAsync();
            await user.MakeAdmin();
            user.RegisterLightningNode("BTC", LightningConnectionType.CLightning);
            var factory = tester.PayTester.GetService<IBTCPayServerClientFactory>();
            Assert.NotNull(factory);
            var client = await factory.Create(user.UserId, user.StoreId);
            await client.GetCurrentUser();
            await client.GetStores();
            var store = await client.GetStore(user.StoreId);
            Assert.NotNull(store);
            var addr = await client.GetLightningDepositAddress(user.StoreId, "BTC");
            Assert.NotNull(BitcoinAddress.Create(addr, Network.RegTest));

            await user.CreateStoreAsync();
            var store1 = user.StoreId;
            await user.CreateStoreAsync();
            var store2 = user.StoreId;
            var store1Client = await factory.Create(null, store1);
            var store2Client = await factory.Create(null, store2);
            var store1Res = await store1Client.GetStore(store1);
            var store2Res = await store2Client.GetStore(store2);
            Assert.Equal(store1, store1Res.Id);
            Assert.Equal(store2, store2Res.Id);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task MissingPermissionTest()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            user.GrantAccess();
            var clientWithWrongPermissions = await user.CreateClient(Policies.CanViewProfile);
            var e = await AssertAPIError("missing-permission", () => clientWithWrongPermissions.CreateStore(new CreateStoreRequest() { Name = "mystore" }));
            Assert.Equal("missing-permission", e.APIError.Code);
            Assert.NotNull(e.APIError.Message);
            GreenfieldPermissionAPIError permissionError = Assert.IsType<GreenfieldPermissionAPIError>(e.APIError);
            Assert.Equal(Policies.CanModifyStoreSettings, permissionError.MissingPermission);

            var client = await user.CreateClient(Policies.CanViewStoreSettings);
            await AssertAPIError("unsupported-in-v2", () => client.SendHttpRequest<object>($"api/v1/stores/{user.StoreId}/payment-methods/LightningNetwork"));
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task ApiKeysControllerTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            user.GrantAccess();
            await user.MakeAdmin();
            var client = await user.CreateClient(Policies.CanViewProfile);
            var clientBasic = await user.CreateClient();
            //Get current api key
            var apiKeyData = await client.GetCurrentAPIKeyInfo();
            Assert.NotNull(apiKeyData);
            Assert.Equal(client.APIKey, apiKeyData.ApiKey);
            Assert.Single(apiKeyData.Permissions);

            //a client using Basic Auth has no business here
            await AssertHttpError(401, async () => await clientBasic.GetCurrentAPIKeyInfo());

            //revoke current api key
            await client.RevokeCurrentAPIKeyInfo();
            await AssertHttpError(401, async () => await client.GetCurrentAPIKeyInfo());
            //a client using Basic Auth has no business here
            await AssertHttpError(401, async () => await clientBasic.RevokeCurrentAPIKeyInfo());
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanUseMiscAPIs()
        {
            using (var tester = CreateServerTester())
            {
                await tester.StartAsync();
                var acc = tester.NewAccount();
                await acc.GrantAccessAsync();
                var unrestricted = await acc.CreateClient();
                var langs = await unrestricted.GetAvailableLanguages();
                Assert.NotEmpty(langs);
                Assert.NotNull(langs[0].Code);
                Assert.NotNull(langs[0].DisplayName);

                var perms = await unrestricted.GetPermissionMetadata();
                Assert.NotEmpty(perms);
                var p = perms.First(p => p.PermissionName == "unrestricted");
                Assert.True(p.SubPermissions.Count > 6);
            }
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task SpecificCanModifyStoreCantCreateNewStore()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var acc = tester.NewAccount();
            await acc.GrantAccessAsync();
            var unrestricted = await acc.CreateClient();
            var response = await unrestricted.CreateStore(new CreateStoreRequest() { Name = "mystore" });
            var apiKey = (await unrestricted.CreateAPIKey(new CreateApiKeyRequest() { Permissions = new[] { Permission.Create("btcpay.store.canmodifystoresettings", response.Id) } })).ApiKey;
            var restricted = new BTCPayServerClient(unrestricted.Host, apiKey);

            // Unscoped permission should be required for create store
            await this.AssertHttpError(403, async () => await restricted.CreateStore(new CreateStoreRequest() { Name = "store2" }));
            // Unrestricted should work fine
            await unrestricted.CreateStore(new CreateStoreRequest() { Name = "store2" });
            // Restricted but unscoped should work fine
            apiKey = (await unrestricted.CreateAPIKey(new CreateApiKeyRequest() { Permissions = new[] { Permission.Create("btcpay.store.canmodifystoresettings") } })).ApiKey;
            restricted = new BTCPayServerClient(unrestricted.Host, apiKey);
            await restricted.CreateStore(new CreateStoreRequest() { Name = "store2" });
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanCreateAndDeleteAPIKeyViaAPI()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var acc = tester.NewAccount();
            await acc.GrantAccessAsync();
            var unrestricted = await acc.CreateClient();
            var apiKey = await unrestricted.CreateAPIKey(new CreateApiKeyRequest()
            {
                Label = "Hello world",
                Permissions = new Permission[] { Permission.Create(Policies.CanViewProfile) }
            });
            Assert.Equal("Hello world", apiKey.Label);
            var p = Assert.Single(apiKey.Permissions);
            Assert.Equal(Policies.CanViewProfile, p.Policy);

            var restricted = acc.CreateClientFromAPIKey(apiKey.ApiKey);
            await AssertHttpError(403,
                async () => await restricted.CreateAPIKey(new CreateApiKeyRequest()
                {
                    Label = "Hello world2",
                    Permissions = new Permission[] { Permission.Create(Policies.CanViewProfile) }
                }));

            await unrestricted.RevokeAPIKey(apiKey.ApiKey);
            await AssertAPIError("apikey-not-found", () => unrestricted.RevokeAPIKey(apiKey.ApiKey));


            // Admin create API key to new user
            acc = tester.NewAccount();
            await acc.GrantAccessAsync(isAdmin: true);
            unrestricted = await acc.CreateClient();
            var newUser = await unrestricted.CreateUser(new CreateApplicationUserRequest
            {
                Email = Utils.GenerateEmail(),
                Password = "Kitten0@",
                Name = "New User",
                ImageUrl = "avatar.jpg"
            });
            var newUserAPIKey = await unrestricted.CreateAPIKey(newUser.Id, new CreateApiKeyRequest()
            {
                Label = "Hello world",
                Permissions = new Permission[] { Permission.Create(Policies.CanViewProfile) }
            });
            var newUserClient = acc.CreateClientFromAPIKey(newUserAPIKey.ApiKey);
            Assert.Equal(newUser.Id, (await newUserClient.GetCurrentUser()).Id);
            Assert.Equal("New User", newUser.Name);
            Assert.Equal("avatar.jpg", newUser.ImageUrl);
            // Admin delete it
            await unrestricted.RevokeAPIKey(newUser.Id, newUserAPIKey.ApiKey);
            await Assert.ThrowsAsync<GreenfieldAPIException>(() => newUserClient.GetCurrentUser());

            // Admin create store
            var store = await unrestricted.CreateStore(new CreateStoreRequest() { Name = "Pouet lol" });

            // Grant right to another user
            newUserAPIKey = await unrestricted.CreateAPIKey(newUser.Email, new CreateApiKeyRequest()
            {
                Label = "Hello world",
                Permissions = new Permission[] { Permission.Create(Policies.CanViewInvoices, store.Id) },
            });

            await AssertAPIError("user-not-found", () => unrestricted.CreateAPIKey("fewiofwuefo", new CreateApiKeyRequest()));

            // Despite the grant, the user shouldn't be able to get the invoices!
            newUserClient = acc.CreateClientFromAPIKey(newUserAPIKey.ApiKey);
            await Assert.ThrowsAsync<GreenfieldAPIException>(() => newUserClient.GetInvoices(store.Id));

            // if user is a guest or owner, then it should be ok
            await unrestricted.AddStoreUser(store.Id, new StoreUserData() { Id = newUser.Id });
            await newUserClient.GetInvoices(store.Id);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanCreateReadAndDeleteFiles()
        {
            using var tester = CreateServerTester(newDb: true);
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.GrantAccessAsync();
            await user.MakeAdmin();
            var client = await user.CreateClient();

            // List
            Assert.Empty(await client.GetFiles());

            // Upload
            var filePath = TestUtils.GetTestDataFullPath("OldInvoices.csv");
            var upload = await client.UploadFile(filePath, "text/csv");
            Assert.Equal("OldInvoices.csv", upload.OriginalName);
            Assert.NotNull(upload.Uri);
            Assert.NotNull(upload.Url);

            // Re-check list
            Assert.Single(await client.GetFiles());

            // Single file endpoint
            var singleFile = await client.GetFile(upload.Id);
            Assert.Equal("OldInvoices.csv", singleFile.OriginalName);
            Assert.NotNull(singleFile.Uri);
            Assert.NotNull(singleFile.Url);

            // Delete
            await client.DeleteFile(upload.Id);
            Assert.Empty(await client.GetFiles());

            // Profile image
            await AssertValidationError(["file"],
                async () => await client.UploadCurrentUserProfilePicture(filePath, "text/csv")
            );

            var profilePath = TestUtils.GetTestDataFullPath("logo.png");
            var currentUser = await client.UploadCurrentUserProfilePicture(profilePath, "image/png");
            var files = await client.GetFiles();
            Assert.Single(files);
            Assert.Equal("logo.png", files[0].OriginalName);
            Assert.Equal(files[0].Url, currentUser.ImageUrl);

            await client.DeleteCurrentUserProfilePicture();
            Assert.Empty(await client.GetFiles());
            currentUser = await client.GetCurrentUser();
            Assert.Null(currentUser.ImageUrl);

            // Store logo
            var store = await client.CreateStore(new CreateStoreRequest { Name = "mystore" });
            await AssertValidationError(["file"],
                async () => await client.UploadStoreLogo(store.Id, filePath, "text/csv")
            );

            var logoPath = TestUtils.GetTestDataFullPath("logo.png");
            var storeData = await client.UploadStoreLogo(store.Id, logoPath, "image/png");
            files = await client.GetFiles();
            Assert.Single(files);
            Assert.Equal("logo.png", files[0].OriginalName);
            Assert.Equal(files[0].Url, storeData.LogoUrl);

            await client.DeleteStoreLogo(store.Id);
            Assert.Empty(await client.GetFiles());
            storeData = await client.GetStore(store.Id);
            Assert.Null(storeData.LogoUrl);

            // App Item Image
            var app = await client.CreatePointOfSaleApp(store.Id, new PointOfSaleAppRequest { AppName = "Test App" });
            await AssertValidationError(["file"],
                async () => await client.UploadAppItemImage(app.Id, filePath, "text/csv")
            );

            var fileData = await client.UploadAppItemImage(app.Id, logoPath, "image/png");
            Assert.Equal("logo.png", fileData.OriginalName);
            files = await client.GetFiles();
            Assert.Single(files);

            await client.DeleteAppItemImage(app.Id, fileData.Id);
            Assert.Empty(await client.GetFiles());
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanCreateReadUpdateAndDeletePointOfSaleApp()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.RegisterDerivationSchemeAsync("BTC");
            var client = await user.CreateClient();

            // Test validation for creating the app
            await AssertValidationError(new[] { "AppName" },
                async () => await client.CreatePointOfSaleApp(user.StoreId, new PointOfSaleAppRequest()));
            await AssertValidationError(new[] { "AppName" },
                async () => await client.CreatePointOfSaleApp(
                    user.StoreId,
                    new PointOfSaleAppRequest
                    {
                        AppName = "this is a really long app name this is a really long app name this is a really long app name",
                    }
                )
            );
            await AssertValidationError(new[] { "Currency" },
                async () => await client.CreatePointOfSaleApp(
                    user.StoreId,
                    new PointOfSaleAppRequest
                    {
                        AppName = "good name",
                        Currency = "fake currency"
                    }
                )
            );
            await AssertValidationError(new[] { "Template" },
                async () => await client.CreatePointOfSaleApp(
                    user.StoreId,
                    new PointOfSaleAppRequest
                    {
                        AppName = "good name",
                        Template = "lol invalid template"
                    }
                )
            );
            await AssertValidationError(new[] { "AppName", "Currency", "Template" },
                async () => await client.CreatePointOfSaleApp(
                    user.StoreId,
                    new PointOfSaleAppRequest
                    {
                        Currency = "fake currency",
                        Template = "lol invalid template"
                    }
                )
            );
            var template = @"[
              {
                ""description"": ""Lovely, fresh and tender, Meng Ding Gan Lu ('sweet dew') is grown in the lush Meng Ding Mountains of the southwestern province of Sichuan where it has been cultivated for over a thousand years."",
                ""id"": ""green-tea"",
                ""image"": ""~/img/pos-sample/green-tea.jpg"",
                ""priceType"": ""Fixed"",
                ""price"": ""1"",
                ""title"": ""Green Tea"",
                ""disabled"": false
              }
            ]";
            await AssertValidationError(new[] { "Template" },
                async () => await client.CreatePointOfSaleApp(
                    user.StoreId,
                    new PointOfSaleAppRequest
                    {
                        AppName = "good name",
                        Template = template.Replace(@"""id"": ""green-tea"",", "")
                    }
                )
            );

            // Test creating a POS app successfully
            var app = await client.CreatePointOfSaleApp(
                user.StoreId,
                new PointOfSaleAppRequest
                {
                    AppName = "test app from API",
                    Currency = "JPY",
                    Title = "test app title",
                    Template = template
                }
            );
            Assert.Equal("test app from API", app.AppName);
            Assert.Equal(user.StoreId, app.StoreId);
            Assert.Equal("PointOfSale", app.AppType);
            Assert.Equal("test app title", app.Title);
            Assert.False(app.Archived);

            // Test title falls back to name
            app = await client.CreatePointOfSaleApp(
                user.StoreId,
                new PointOfSaleAppRequest
                {
                    AppName = "test app name",
                    Description = "test description",
                    ShowItems = true,
                    ShowCategories = false
                }
            );
            Assert.Equal("test app name", app.Title);
            Assert.Equal("test description", app.Description);
            Assert.True(app.ShowItems);
            Assert.False(app.ShowCategories);
            Assert.False(app.ShowDiscount);

            // Make sure we return a 404 if we try to get an app that doesn't exist
            await AssertHttpError(404, async () =>
            {
                await client.GetApp("some random ID lol");
            });
            await AssertHttpError(404, async () =>
            {
                await client.GetPosApp("some random ID lol");
            });

            // Test that we can retrieve the app data
            var retrievedApp = await client.GetApp(app.Id);
            Assert.Equal(app.AppName, retrievedApp.AppName);
            Assert.Equal(app.StoreId, retrievedApp.StoreId);
            Assert.Equal(app.AppType, retrievedApp.AppType);

            // Test that we can update the app data
            var retrievedPosApp = await client.UpdatePointOfSaleApp(
                app.Id,
                new PointOfSaleAppRequest
                {
                    AppName = "new app name",
                    Title = "new app title",
                    Archived = true
                }
            );
            Assert.Equal("new app name", retrievedPosApp.AppName);
            Assert.Equal("new app title", retrievedPosApp.Title);
            Assert.True(retrievedPosApp.Archived);

            // Test generic GET app endpoint first
            retrievedApp = await client.GetApp(app.Id);
            Assert.Equal("new app name", retrievedApp.AppName);
            Assert.True(retrievedApp.Archived);

            // Test the POS-specific endpoint also
            retrievedPosApp = await client.GetPosApp(app.Id);
            Assert.Equal("new app name", retrievedPosApp.AppName);
            Assert.Equal("new app title", retrievedPosApp.Title);

            // Make sure we return a 404 if we try to delete an app that doesn't exist
            await AssertHttpError(404, async () =>
            {
                await client.DeleteApp("some random ID lol");
            });

            // Test deleting the newly created app
            await client.DeleteApp(retrievedApp.Id);
            await AssertHttpError(404, async () =>
            {
                await client.GetApp(retrievedApp.Id);
            });
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanCreateReadAndDeleteCrowdfundApp()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.RegisterDerivationSchemeAsync("BTC");
            var client = await user.CreateClient();

            // Test validation for creating the app
            await AssertValidationError(new[] { "AppName" },
                async () => await client.CreateCrowdfundApp(user.StoreId, new CrowdfundAppRequest()));
            await AssertValidationError(new[] { "AppName" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "this is a really long app name this is a really long app name this is a really long app name",
                    }
                )
            );
            await AssertValidationError(new[] { "TargetCurrency" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        TargetCurrency = "fake currency"
                    }
                )
            );
            await AssertValidationError(new[] { "PerksTemplate" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        PerksTemplate = "lol invalid template"
                    }
                )
            );
            await AssertValidationError(new[] { "AppName", "TargetCurrency", "PerksTemplate" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        TargetCurrency = "fake currency",
                        PerksTemplate = "lol invalid template"
                    }
                )
            );
            await AssertValidationError(new[] { "AnimationColors" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        AnimationColors = new string[] { }
                    }
                )
            );
            await AssertValidationError(new[] { "AnimationColors" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        AnimationColors = new string[] { "  ", " " }
                    }
                )
            );
            await AssertValidationError(new[] { "Sounds" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        Sounds = new string[] { "  " }
                    }
                )
            );
            await AssertValidationError(new[] { "Sounds" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        Sounds = new string[] { " ", " ", " " }
                    }
                )
            );
            await AssertValidationError(new[] { "EndDate" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        StartDate = DateTime.Parse("1998-01-01"),
                        EndDate = DateTime.Parse("1997-12-31")
                    }
                )
            );
            var template = @"[
              {
                ""description"": ""Lovely, fresh and tender, Meng Ding Gan Lu ('sweet dew') is grown in the lush Meng Ding Mountains of the southwestern province of Sichuan where it has been cultivated for over a thousand years."",
                ""id"": ""green-tea"",
                ""image"": ""~/img/pos-sample/green-tea.jpg"",
                ""priceType"": ""Fixed"",
                ""price"": ""1"",
                ""title"": ""Green Tea"",
                ""disabled"": false
              }
            ]";
            await AssertValidationError(new[] { "PerksTemplate" },
                async () => await client.CreateCrowdfundApp(
                    user.StoreId,
                    new CrowdfundAppRequest
                    {
                        AppName = "good name",
                        PerksTemplate = template.Replace(@"""id"": ""green-tea"",", "")
                    }
                )
            );

            // Test creating a crowdfund app
            var app = await client.CreateCrowdfundApp(
                user.StoreId,
                new CrowdfundAppRequest
                {
                    AppName = "test app from API",
                    Title = "test app title",
                    PerksTemplate = template
                }
            );
            Assert.Equal("test app from API", app.AppName);
            Assert.Equal(user.StoreId, app.StoreId);
            Assert.Equal("Crowdfund", app.AppType);
            Assert.False(app.Archived);

            // Test title falls back to name
            app = await client.CreateCrowdfundApp(
                user.StoreId,
                new CrowdfundAppRequest
                {
                    AppName = "test app name",
                    Description = "test description"
                }
            );
            Assert.Equal("test app name", app.Title);

            // Make sure we return a 404 if we try to get an app that doesn't exist
            await AssertHttpError(404, async () =>
            {
                await client.GetApp("some random ID lol");
            });
            await AssertHttpError(404, async () =>
            {
                await client.GetCrowdfundApp("some random ID lol");
            });

            // Test that we can retrieve the app data
            var retrievedApp = await client.GetApp(app.Id);
            Assert.Equal(app.AppName, retrievedApp.AppName);
            Assert.Equal(app.StoreId, retrievedApp.StoreId);
            Assert.Equal(app.AppType, retrievedApp.AppType);
            Assert.False(retrievedApp.Archived);

            // Test the crowdfund-specific endpoint also
            var retrievedCfApp = await client.GetCrowdfundApp(app.Id);
            Assert.Equal(app.AppName, retrievedCfApp.AppName);
            Assert.Equal(app.Title, retrievedCfApp.Title);
            Assert.Equal("test description", retrievedCfApp.Description);
            Assert.False(retrievedCfApp.Archived);

            // Make sure we return a 404 if we try to delete an app that doesn't exist
            await AssertHttpError(404, async () =>
            {
                await client.DeleteApp("some random ID lol");
            });

            // Test deleting the newly created app
            await client.DeleteApp(retrievedApp.Id);
            await AssertHttpError(404, async () =>
            {
                await client.GetApp(retrievedApp.Id);
            });
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanGetAllApps()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.RegisterDerivationSchemeAsync("BTC");
            var client = await user.CreateClient();

            var posApp = await client.CreatePointOfSaleApp(
                user.StoreId,
                new PointOfSaleAppRequest
                {
                    AppName = "test app from API",
                    Currency = "JPY"
                }
            );
            var crowdfundApp = await client.CreateCrowdfundApp(user.StoreId, new CrowdfundAppRequest { AppName = "test app from API" });

            // Create another store and one app on it so we can get all apps from all stores for the user below
            var newStore = await client.CreateStore(new CreateStoreRequest { Name = "A" });
            var newApp = await client.CreateCrowdfundApp(newStore.Id, new CrowdfundAppRequest { AppName = "new app" });

            Assert.NotEqual(newApp.Id, user.StoreId);

            // Get all apps for a specific store first
            var apps = await client.GetAllApps(user.StoreId);

            Assert.Equal(2, apps.Length);

            Assert.Equal(posApp.AppName, apps[0].AppName);
            Assert.Equal(posApp.StoreId, apps[0].StoreId);
            Assert.Equal(posApp.AppType, apps[0].AppType);
            Assert.False(apps[0].Archived);

            Assert.Equal(crowdfundApp.AppName, apps[1].AppName);
            Assert.Equal(crowdfundApp.StoreId, apps[1].StoreId);
            Assert.Equal(crowdfundApp.AppType, apps[1].AppType);
            Assert.False(apps[1].Archived);

            // Get all apps for all store now
            apps = await client.GetAllApps();

            Assert.Equal(3, apps.Length);

            Assert.Equal(posApp.AppName, apps[0].AppName);
            Assert.Equal(posApp.StoreId, apps[0].StoreId);
            Assert.Equal(posApp.AppType, apps[0].AppType);
            Assert.False(apps[0].Archived);

            Assert.Equal(crowdfundApp.AppName, apps[1].AppName);
            Assert.Equal(crowdfundApp.StoreId, apps[1].StoreId);
            Assert.Equal(crowdfundApp.AppType, apps[1].AppType);
            Assert.False(apps[1].Archived);

            Assert.Equal(newApp.AppName, apps[2].AppName);
            Assert.Equal(newApp.StoreId, apps[2].StoreId);
            Assert.Equal(newApp.AppType, apps[2].AppType);
            Assert.False(apps[2].Archived);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanGetAppStats()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);
            await user.MakeAdmin();
            await user.RegisterDerivationSchemeAsync("BTC");
            var client = await user.CreateClient();

            var item1 = new AppItem { Id = "item1", Title = "Item 1", Price = 1, PriceType = AppItemPriceType.Fixed };
            var item2 = new AppItem { Id = "item2", Title = "Item 2", Price = 2, PriceType = AppItemPriceType.Fixed };
            var item3 = new AppItem { Id = "item3", Title = "Item 3", Price = 3, PriceType = AppItemPriceType.Fixed };
            var posItems = AppService.SerializeTemplate([item1, item2, item3]);
            var posApp = await client.CreatePointOfSaleApp(user.StoreId, new PointOfSaleAppRequest { AppName = "test pos", Template = posItems, });
            var crowdfundApp = await client.CreateCrowdfundApp(user.StoreId, new CrowdfundAppRequest { AppName = "test crowdfund" });

            // empty states
            var posSales = await client.GetAppSales(posApp.Id);
            Assert.NotNull(posSales);
            Assert.Equal(0, posSales.SalesCount);
            Assert.Equal(7, posSales.Series.Count());

            var crowdfundSales = await client.GetAppSales(crowdfundApp.Id);
            Assert.NotNull(crowdfundSales);
            Assert.Equal(0, crowdfundSales.SalesCount);
            Assert.Equal(7, crowdfundSales.Series.Count());

            var posTopItems = await client.GetAppTopItems(posApp.Id);
            Assert.Empty(posTopItems);

            var crowdfundItems = await client.GetAppTopItems(crowdfundApp.Id);
            Assert.Empty(crowdfundItems);

            // with sales - fiddle invoices via the UI controller
            var uiPosController = tester.PayTester.GetController<UIPointOfSaleController>();

            var action = Assert.IsType<RedirectToActionResult>(uiPosController.ViewPointOfSale(posApp.Id, PosViewType.Static, 1, choiceKey: item1.Id).GetAwaiter().GetResult());
            Assert.Equal(nameof(UIInvoiceController.Checkout), action.ActionName);
            Assert.True(action.RouteValues!.TryGetValue("invoiceId", out var i1Id));

            var cart = new JObject {
                ["cart"] = new JArray
                {
                    new JObject { ["id"] = item2.Id, ["count"] = 4 },
                    new JObject { ["id"] = item3.Id, ["count"] = 2 }
                },
                ["subTotal"] = 14,
                ["total"] = 14,
                ["amounts"] = new JArray()
            }.ToString();
            action = Assert.IsType<RedirectToActionResult>(uiPosController.ViewPointOfSale(posApp.Id, PosViewType.Cart, 7, posData: cart).GetAwaiter().GetResult());
            Assert.Equal(nameof(UIInvoiceController.Checkout), action.ActionName);
            Assert.True(action.RouteValues!.TryGetValue("invoiceId", out var i2Id));

            await user.PayInvoice(i1Id!.ToString());
            await user.PayInvoice(i2Id!.ToString());

            posSales = await client.GetAppSales(posApp.Id);
            Assert.Equal(7, posSales.SalesCount);
            Assert.Equal(7, posSales.Series.Count());
            Assert.Equal(0, posSales.Series.First().SalesCount);
            Assert.Equal(7, posSales.Series.Last().SalesCount);

            posTopItems = await client.GetAppTopItems(posApp.Id);
            Assert.Equal(3, posTopItems.Count);
            Assert.Equal(item2.Id, posTopItems[0].ItemCode);
            Assert.Equal(4, posTopItems[0].SalesCount);

            Assert.Equal(item3.Id, posTopItems[1].ItemCode);
            Assert.Equal(2, posTopItems[1].SalesCount);

            Assert.Equal(item1.Id, posTopItems[2].ItemCode);
            Assert.Equal(1, posTopItems[2].SalesCount);

            // with count and offset
            posTopItems = await client.GetAppTopItems(posApp.Id,1, 5);
            Assert.Equal(2, posTopItems.Count);
            Assert.Equal(item3.Id, posTopItems[0].ItemCode);
            Assert.Equal(2, posTopItems[0].SalesCount);

            Assert.Equal(item1.Id, posTopItems[1].ItemCode);
            Assert.Equal(1, posTopItems[1].SalesCount);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanDeleteUsersViaApi()
        {
            using var tester = CreateServerTester(newDb: true);
            await tester.StartAsync();
            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);
            // Should not be authorized to perform this action
            await AssertHttpError(401,
                async () => await unauthClient.DeleteUser("lol user id"));

            var user = tester.NewAccount();
            await user.GrantAccessAsync();
            await user.MakeAdmin();
            var adminClient = await user.CreateClient(Policies.Unrestricted);

            //can't delete if the only admin
            await AssertHttpError(403,
                async () => await adminClient.DeleteCurrentUser());

            // Should 404 if user doesn't exist
            await AssertHttpError(404,
                async () => await adminClient.DeleteUser("lol user id"));

            user = tester.NewAccount();
            await user.GrantAccessAsync();
            var badClient = await user.CreateClient(Policies.CanCreateInvoice);

            await AssertHttpError(403,
                async () => await badClient.DeleteCurrentUser());

            var goodClient = await user.CreateClient(Policies.CanDeleteUser, Policies.CanViewProfile);
            await goodClient.DeleteCurrentUser();
            await AssertHttpError(404,
                async () => await adminClient.DeleteUser(user.UserId));

            tester.Stores.Remove(user.StoreId);
        }


        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanViewUsersViaApi()
        {
            using var tester = CreateServerTester(newDb: true);
            await tester.StartAsync();

            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);

            // Should be 401 for all calls because we don't have permission
            await AssertHttpError(401, async () => await unauthClient.GetUsers());
            await AssertHttpError(401, async () => await unauthClient.GetUserByIdOrEmail("non_existing_id"));
            await AssertHttpError(401, async () => await unauthClient.GetUserByIdOrEmail("someone@example.com"));


            var adminUser = tester.NewAccount();
            await adminUser.GrantAccessAsync();
            await adminUser.MakeAdmin();
            var adminClient = await adminUser.CreateClient(Policies.Unrestricted);

            // Should be 404 if user doesn't exist
            await AssertHttpError(404, async () => await adminClient.GetUserByIdOrEmail("non_existing_id"));
            await AssertHttpError(404, async () => await adminClient.GetUserByIdOrEmail("doesnotexist@example.com"));

            // Try listing all users, should be fine
            await adminClient.GetUsers();

            // Try loading 1 user by ID. Loading myself.
            await adminClient.GetUserByIdOrEmail(adminUser.UserId);

            // Try loading 1 user by email. Loading myself.
            await adminClient.GetUserByIdOrEmail(adminUser.Email);


            // var badClient = await user.CreateClient(Policies.CanCreateInvoice);
            // await AssertHttpError(403,
            //     async () => await badClient.DeleteCurrentUser());

            var goodUser = tester.NewAccount();
            await goodUser.GrantAccessAsync();
            await goodUser.MakeAdmin();
            var goodClient = await goodUser.CreateClient(Policies.CanViewUsers);

            // Try listing all users, should be fine
            await goodClient.GetUsers();

            // Should be 404 if user doesn't exist
            await AssertHttpError(404, async () => await goodClient.GetUserByIdOrEmail("non_existing_id"));
            await AssertHttpError(404, async () => await goodClient.GetUserByIdOrEmail("doesnotexist@example.com"));

            // Try listing all users, should be fine
            await goodClient.GetUsers();

            // Try loading 1 user by ID. Loading myself.
            await goodClient.GetUserByIdOrEmail(goodUser.UserId);

            // Try loading 1 user by email. Loading myself.
            await goodClient.GetUserByIdOrEmail(goodUser.Email);




            var badUser = tester.NewAccount();
            await badUser.GrantAccessAsync();
            await badUser.MakeAdmin();

            // Bad user has a permission, but it's the wrong one.
            var badClient = await goodUser.CreateClient(Policies.CanCreateInvoice);

            // Try listing all users, should be fine
            await AssertHttpError(403, async () => await badClient.GetUsers());

            // Should be 404 if user doesn't exist
            await AssertHttpError(403, async () => await badClient.GetUserByIdOrEmail("non_existing_id"));
            await AssertHttpError(403, async () => await badClient.GetUserByIdOrEmail("doesnotexist@example.com"));

            // Try listing all users, should be fine
            await AssertHttpError(403, async () => await badClient.GetUsers());

            // Try loading 1 user by ID. Loading myself.
            await AssertHttpError(403, async () => await badClient.GetUserByIdOrEmail(badUser.UserId));

            // Try loading 1 user by email. Loading myself.
            await AssertHttpError(403, async () => await badClient.GetUserByIdOrEmail(badUser.Email));

            // Why is this line needed? I saw it in "CanDeleteUsersViaApi" as well. Is this part of the cleanup?
            tester.Stores.Remove(adminUser.StoreId);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanCreateUsersViaAPI()
        {
            using var tester = CreateServerTester(newDb: true);
            tester.PayTester.DisableRegistration = true;
            await tester.StartAsync();
            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);
            await AssertValidationError(new[] { "Email" },
                async () => await unauthClient.CreateUser(new CreateApplicationUserRequest()));

            // We have no admin, so it should work
            var user1 = await unauthClient.CreateUser(
                new CreateApplicationUserRequest() { Email = "test@gmail.com", Password = "abceudhqw" });
            Assert.Empty(user1.Roles);

            // We have no admin, so it should work
            var user2 = await unauthClient.CreateUser(
                new CreateApplicationUserRequest() { Email = "test2@gmail.com", Password = "abceudhqw" });
            Assert.Empty(user2.Roles);

            // Duplicate email
            await AssertValidationError(new[] { "Email" },
                async () => await unauthClient.CreateUser(
                    new CreateApplicationUserRequest() { Email = "test2@gmail.com", Password = "abceudhqw" }));

            // Let's make an admin
            var admin = await unauthClient.CreateUser(new CreateApplicationUserRequest()
            {
                Email = "admin@gmail.com",
                Password = "abceudhqw",
                IsAdministrator = true
            });
            Assert.Contains("ServerAdmin", admin.Roles);
            Assert.NotNull(admin.Created);
            Assert.True((DateTimeOffset.Now - admin.Created).Value.Seconds < 10);

            // Creating a new user without proper creds is now impossible (unauthorized)
            // Because if registration are locked and that an admin exists, we don't accept unauthenticated connection
            var ex = await AssertAPIError("unauthenticated",
                async () => await unauthClient.CreateUser(
                    new CreateApplicationUserRequest() { Email = "test3@gmail.com", Password = "afewfoiewiou" }));
            Assert.Equal("New user creation isn't authorized to users who are not admin", ex.APIError.Message);

            // But should be ok with subscriptions unlocked
            var settings = tester.PayTester.GetService<SettingsRepository>();
            await settings.UpdateSetting<PoliciesSettings>(new PoliciesSettings() { LockSubscription = false });
            await unauthClient.CreateUser(
                new CreateApplicationUserRequest() { Email = "test3@gmail.com", Password = "afewfoiewiou" });

            // But it should be forbidden to create an admin without being authenticated
            await AssertHttpError(401,
                async () => await unauthClient.CreateUser(new CreateApplicationUserRequest()
                {
                    Email = "admin2@gmail.com",
                    Password = "afewfoiewiou",
                    IsAdministrator = true
                }));
            await settings.UpdateSetting<PoliciesSettings>(new PoliciesSettings() { LockSubscription = true });

            var adminAcc = tester.NewAccount();
            adminAcc.UserId = admin.Id;
            adminAcc.IsAdmin = true;
            var adminClient = await adminAcc.CreateClient(Policies.CanModifyProfile);

            // We should be forbidden to create a new user without proper admin permissions
            await AssertHttpError(403,
                async () => await adminClient.CreateUser(
                    new CreateApplicationUserRequest() { Email = "test4@gmail.com", Password = "afewfoiewiou" }));
            await AssertAPIError("missing-permission",
                async () => await adminClient.CreateUser(new CreateApplicationUserRequest()
                {
                    Email = "test4@gmail.com",
                    Password = "afewfoiewiou",
                    IsAdministrator = true
                }));

            // However, should be ok with the unrestricted permissions of an admin
            adminClient = await adminAcc.CreateClient(Policies.Unrestricted);
            await adminClient.CreateUser(
                new CreateApplicationUserRequest() { Email = "test4@gmail.com", Password = "afewfoiewiou" });
            // Even creating new admin should be ok
            await adminClient.CreateUser(new CreateApplicationUserRequest()
            {
                Email = "admin4@gmail.com",
                Password = "afewfoiewiou",
                IsAdministrator = true
            });

            // Create user without password
            await adminClient.CreateUser(new CreateApplicationUserRequest
            {
                Email = "nopassword@gmail.com"
            });

            // Regular user
            var user1Acc = tester.NewAccount();
            user1Acc.UserId = user1.Id;
            user1Acc.IsAdmin = false;
            var user1Client = await user1Acc.CreateClient(Policies.CanModifyServerSettings);

            // User1 trying to get server management would still fail to create user
            await AssertHttpError(403,
                async () => await user1Client.CreateUser(
                    new CreateApplicationUserRequest() { Email = "test8@gmail.com", Password = "afewfoiewiou" }));

            // User1 should be able to create user if subscription unlocked
            await settings.UpdateSetting<PoliciesSettings>(new PoliciesSettings() { LockSubscription = false });
            await user1Client.CreateUser(
                new CreateApplicationUserRequest() { Email = "test8@gmail.com", Password = "afewfoiewiou" });

            // But not an admin
            await AssertHttpError(403,
                async () => await user1Client.CreateUser(new CreateApplicationUserRequest()
                {
                    Email = "admin8@gmail.com",
                    Password = "afewfoiewiou",
                    IsAdministrator = true
                }));

            // If we set DisableNonAdminCreateUserApi = true, it should always fail to create a user unless you are an admin
            await settings.UpdateSetting(new PoliciesSettings() { LockSubscription = false, DisableNonAdminCreateUserApi = true });
            await AssertHttpError(403,
                async () =>
                    await unauthClient.CreateUser(
                        new CreateApplicationUserRequest() { Email = "test9@gmail.com", Password = "afewfoiewiou" }));
            await AssertHttpError(403,
                async () =>
                    await user1Client.CreateUser(
                        new CreateApplicationUserRequest() { Email = "test9@gmail.com", Password = "afewfoiewiou" }));
            await adminClient.CreateUser(
                new CreateApplicationUserRequest() { Email = "test9@gmail.com", Password = "afewfoiewiou" });
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanUpdateUsersViaAPI()
        {
            using var tester = CreateServerTester(newDb: true);
            tester.PayTester.DisableRegistration = true;
            await tester.StartAsync();
            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);

            // We have no admin, so it should work
            var user = await unauthClient.CreateUser(
                new CreateApplicationUserRequest { Email = "test@gmail.com", Password = "abceudhqw" });
            Assert.Empty(user.Roles);

            // We have no admin, so it should work
            var admin = await unauthClient.CreateUser(
                new CreateApplicationUserRequest { Email = "admin@gmail.com", Password = "abceudhqw", IsAdministrator = true });
            Assert.Contains("ServerAdmin", admin.Roles);

            var adminAcc = tester.NewAccount();
            adminAcc.UserId = admin.Id;
            adminAcc.IsAdmin = true;
            var adminClient = await adminAcc.CreateClient(Policies.CanModifyProfile);

            // Invalid email
            await AssertValidationError(["Email"],
                async () => await adminClient.UpdateCurrentUser(
                    new UpdateApplicationUserRequest { Email = "test@" }));
            await AssertValidationError(["Email"],
                async () => await adminClient.UpdateCurrentUser(
                    new UpdateApplicationUserRequest { Email = "Firstname Lastname <blah@example.com>" }));

            // Duplicate email
            await AssertValidationError(["Email"],
                async () => await adminClient.UpdateCurrentUser(
                    new UpdateApplicationUserRequest { Email = "test@gmail.com" }));

            // Invalid current password
            await AssertValidationError(["CurrentPassword"],
                async () => await adminClient.UpdateCurrentUser(
                    new UpdateApplicationUserRequest { Email = "test@gmail.com", CurrentPassword = "123", NewPassword = "abceudhqw123"}));

            // Change properties with valid state
            var changed = await adminClient.UpdateCurrentUser(
                new UpdateApplicationUserRequest
                {
                    Email = "administrator@gmail.com",
                    CurrentPassword = "abceudhqw",
                    NewPassword = "abceudhqw123",
                    Name = "Changed Admin",
                    ImageUrl = "avatar.jpg"
                });
            Assert.Equal("administrator@gmail.com", changed.Email);
            Assert.Equal("Changed Admin", changed.Name);
            Assert.Equal("avatar.jpg", changed.ImageUrl);
        }

        [Fact]
        [Trait("Integration", "Integration")]
        public async Task CanUsePullPaymentViaAPI()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var acc = tester.NewAccount();
            await acc.GrantAccessAsync(true);
            acc.RegisterLightningNode("BTC", LightningConnectionType.CLightning, false);
            var storeId = (await acc.RegisterDerivationSchemeAsync("BTC", importKeysToNBX: true)).StoreId;
            var client = await acc.CreateClient();
            var result = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest()
            {
                Name = "Test",
                Description = "Test description",
                Amount = 12.3m,
                Currency = "BTC",
                PayoutMethods = new[] { "BTC" }
            });

            void VerifyResult()
            {
                Assert.Equal("Test", result.Name);
                Assert.Equal("Test description", result.Description);
                // If it contains ? it means that we are resolving an unknown route with the link generator
                Assert.DoesNotContain("?", result.ViewLink);
                Assert.False(result.Archived);
                Assert.Equal("BTC", result.Currency);
                Assert.Equal(12.3m, result.Amount);
            }
            VerifyResult();

            var unauthenticated = new BTCPayServerClient(tester.PayTester.ServerUri);
            result = await unauthenticated.GetPullPayment(result.Id);
            VerifyResult();
            await AssertHttpError(404, async () => await unauthenticated.GetPullPayment("lol"));
            // Can't list pull payments unauthenticated
            await AssertHttpError(401, async () => await unauthenticated.GetPullPayments(storeId));

            var pullPayments = await client.GetPullPayments(storeId);
            result = Assert.Single(pullPayments);
            VerifyResult();

            var test2 = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Test 2",
                Amount = 12.3m,
                Currency = "BTC",
                PayoutMethods = new[] { "BTC" },
                BOLT11Expiration = TimeSpan.FromDays(31.0)
            });
            Assert.Equal(TimeSpan.FromDays(31.0), test2.BOLT11Expiration);

            TestLogs.LogInformation("Can't archive without knowing the walletId");
            var ex = await AssertAPIError("missing-permission", async () => await client.ArchivePullPayment("lol", result.Id));
            Assert.Equal("btcpay.store.canarchivepullpayments", ((GreenfieldPermissionAPIError)ex.APIError).MissingPermission);
            TestLogs.LogInformation("Can't archive without permission");
            await AssertAPIError("unauthenticated", async () => await unauthenticated.ArchivePullPayment(storeId, result.Id));
            await client.ArchivePullPayment(storeId, result.Id);
            result = await unauthenticated.GetPullPayment(result.Id);
            Assert.Equal(TimeSpan.FromDays(30.0), result.BOLT11Expiration);
            Assert.True(result.Archived);
            var pps = await client.GetPullPayments(storeId);
            result = Assert.Single(pps);
            Assert.Equal("Test 2", result.Name);
            pps = await client.GetPullPayments(storeId, true);
            Assert.Equal(2, pps.Length);
            Assert.Equal("Test 2", pps[0].Name);
            Assert.Equal("Test", pps[1].Name);

            var payouts = await unauthenticated.GetPayouts(pps[0].Id);
            Assert.Empty(payouts);

            var destination = (await tester.ExplorerNode.GetNewAddressAsync()).ToString();
            await this.AssertAPIError("overdraft", async () => await unauthenticated.CreatePayout(pps[0].Id, new CreatePayoutRequest()
            {
                Destination = destination,
                Amount = 1_000_000m,
                PayoutMethodId = "BTC",
            }));

            await this.AssertAPIError("archived", async () => await unauthenticated.CreatePayout(pps[1].Id, new CreatePayoutRequest()
            {
                Destination = destination,
                PayoutMethodId = "BTC"
            }));

            var payout = await unauthenticated.CreatePayout(pps[0].Id, new CreatePayoutRequest()
            {
                Destination = destination,
                PayoutMethodId = "BTC"
            });

            payouts = await unauthenticated.GetPayouts(pps[0].Id);
            var payout2 = Assert.Single(payouts);
            Assert.Equal(payout.OriginalAmount, payout2.OriginalAmount);
            Assert.Equal(payout.Id, payout2.Id);
            Assert.Equal(destination, payout2.Destination);
            Assert.Equal(PayoutState.AwaitingApproval, payout.State);
            Assert.Equal("BTC-CHAIN", payout2.PayoutMethodId);
            Assert.Equal("BTC", payout2.PayoutCurrency);
            Assert.Null(payout.PayoutAmount);

            TestLogs.LogInformation("Can't overdraft");

            var destination2 = (await tester.ExplorerNode.GetNewAddressAsync()).ToString();
            await this.AssertAPIError("overdraft", async () => await unauthenticated.CreatePayout(pps[0].Id, new CreatePayoutRequest()
            {
                Destination = destination2,
                Amount = 0.00001m,
                PayoutMethodId = "BTC"
            }));

            TestLogs.LogInformation("Can't create too low payout");
            await this.AssertAPIError("amount-too-low", async () => await unauthenticated.CreatePayout(pps[0].Id, new CreatePayoutRequest()
            {
                Destination = destination2,
                PayoutMethodId = "BTC"
            }));

            TestLogs.LogInformation("Can archive payout");
            await client.CancelPayout(storeId, payout.Id);
            payouts = await unauthenticated.GetPayouts(pps[0].Id);
            Assert.Empty(payouts);

            payouts = await client.GetPayouts(pps[0].Id, true);
            payout = Assert.Single(payouts);
            Assert.Equal(PayoutState.Cancelled, payout.State);

            TestLogs.LogInformation("Can create payout after cancelling");
            payout = await unauthenticated.CreatePayout(pps[0].Id, new CreatePayoutRequest()
            {
                Destination = destination,
                PayoutMethodId = "BTC"
            });

            var start = RoundSeconds(DateTimeOffset.Now + TimeSpan.FromDays(7.0));
            var inFuture = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Starts in the future",
                Amount = 12.3m,
                StartsAt = start,
                Currency = "BTC",
                PayoutMethods = new[] { "BTC" }
            });
            Assert.Equal(start, inFuture.StartsAt);
            Assert.Null(inFuture.ExpiresAt);
            await this.AssertAPIError("not-started", async () => await unauthenticated.CreatePayout(inFuture.Id, new CreatePayoutRequest()
            {
                Amount = 1.0m,
                Destination = destination,
                PayoutMethodId = "BTC"
            }));

            var expires = RoundSeconds(DateTimeOffset.Now - TimeSpan.FromDays(7.0));
            var inPast = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Will expires",
                Amount = 12.3m,
                ExpiresAt = expires,
                Currency = "BTC",
                PayoutMethods = new[] { "BTC" }
            });
            await this.AssertAPIError("expired", async () => await unauthenticated.CreatePayout(inPast.Id, new CreatePayoutRequest()
            {
                Amount = 1.0m,
                Destination = destination,
                PayoutMethodId = "BTC"
            }));

            await this.AssertValidationError(new[] { "ExpiresAt" }, async () => await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Test 2",
                Amount = 12.3m,
                StartsAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1)
            }));


            TestLogs.LogInformation("Create a pull payment with USD");
            var pp = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Test USD",
                Amount = 5000m,
                Currency = "USD",
                PayoutMethods = new[] { "BTC" }
            });

            await this.AssertAPIError("lnurl-not-supported", async () => await unauthenticated.GetPullPaymentLNURL(pp.Id));

            destination = (await tester.ExplorerNode.GetNewAddressAsync()).ToString();
            TestLogs.LogInformation("Try to pay it in BTC");
            payout = await unauthenticated.CreatePayout(pp.Id, new CreatePayoutRequest()
            {
                Destination = destination,
                PayoutMethodId = "BTC"
            });
            await this.AssertAPIError("old-revision", async () => await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest()
            {
                Revision = -1
            }));
            await this.AssertAPIError("rate-unavailable", async () => await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest()
            {
                RateRule = "DONOTEXIST(BTC_USD)"
            }));
            payout = await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest()
            {
                Revision = payout.Revision
            });
            Assert.Equal(PayoutState.AwaitingPayment, payout.State);
            Assert.NotNull(payout.PayoutAmount);
            Assert.Equal(1.0m, payout.PayoutAmount); // 1 BTC == 5000 USD in tests
            await this.AssertAPIError("invalid-state", async () => await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest()
            {
                Revision = payout.Revision
            }));

            // Create one pull payment with an amount of 9 decimals
            var test3 = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Test 2",
                Amount = 12.303228134m,
                Currency = "BTC",
                PayoutMethods = new[] { "BTC" }
            });
            destination = (await tester.ExplorerNode.GetNewAddressAsync()).ToString();
            payout = await unauthenticated.CreatePayout(test3.Id, new CreatePayoutRequest()
            {
                Destination = destination,
                PayoutMethodId = "BTC"
            });
            payout = await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());
            // The payout should round the value of the payment down to the network of the payment method
            Assert.Equal(12.30322814m, payout.PayoutAmount);
            Assert.Equal(12.303228134m, payout.OriginalAmount);

            await client.MarkPayoutPaid(storeId, payout.Id);
            payout = (await client.GetPayouts(payout.PullPaymentId)).First(data => data.Id == payout.Id);
            Assert.Equal(PayoutState.Completed, payout.State);
            await AssertAPIError("invalid-state", async () => await client.MarkPayoutPaid(storeId, payout.Id));

            // Test LNURL values
            var test4 = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Test 3",
                Amount = 12.303228134m,
                Currency = "BTC",
                PayoutMethods = new[] { "BTC", "BTC-LightningNetwork", "BTC_LightningLike" }
            });
            var lnrURLs = await unauthenticated.GetPullPaymentLNURL(test4.Id);
            Assert.IsType<string>(lnrURLs.LNURLBech32);
            Assert.IsType<string>(lnrURLs.LNURLUri);
            Assert.Equal(12.303228134m, test4.Amount);
            Assert.Equal("BTC", test4.Currency);

            // Check we can register Boltcard
            var uid = new byte[7];
            RandomNumberGenerator.Fill(uid);
            var card = await client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                UID = uid
            });
            Assert.Equal(0, card.Version);
            var card1keys = new[] { card.K0, card.K1, card.K2, card.K3, card.K4 };
            Assert.DoesNotContain(null, card1keys);

            var card2 = await client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                UID = uid
            });
            Assert.Equal(0, card2.Version);
            card2 = await client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                UID = uid,
                OnExisting = OnExistingBehavior.UpdateVersion
            });
            Assert.Equal(1, card2.Version);
            Assert.StartsWith("lnurlw://", card2.LNURLW);
            Assert.EndsWith("/boltcard", card2.LNURLW);
            var card2keys = new[] { card2.K0, card2.K1, card2.K2, card2.K3, card2.K4 };
            Assert.DoesNotContain(null, card2keys);
            for (int i = 0; i < card1keys.Length; i++)
            {
                if (i == 1)
                    Assert.Contains(card1keys[i], card2keys);
                else
                    Assert.DoesNotContain(card1keys[i], card2keys);
            }
            var card3 = await client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                UID = uid,
                OnExisting = OnExistingBehavior.KeepVersion
            });
            Assert.Equal(card2.Version, card3.Version);
            var p = new byte[] { 0xc7 }.Concat(uid).Concat(new byte[8]).ToArray();
            var card4 = await client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                OnExisting = OnExistingBehavior.KeepVersion,
                LNURLW = card2.LNURLW + $"?p={Encoders.Hex.EncodeData(AESKey.Parse(card2.K1).Encrypt(p))}"
            });
            Assert.Equal(card2.Version, card4.Version);
            Assert.Equal(card2.K4, card4.K4);
            // Can't define both properties
            await AssertValidationError(["LNURLW"], () => client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                OnExisting = OnExistingBehavior.KeepVersion,
                UID = uid,
                LNURLW = card2.LNURLW + $"?p={Encoders.Hex.EncodeData(AESKey.Parse(card2.K1).Encrypt(p))}"
            }));
            // p is malformed
            await AssertValidationError(["LNURLW"], () => client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                OnExisting = OnExistingBehavior.KeepVersion,
                UID = uid,
                LNURLW = card2.LNURLW + $"?p=lol"
            }));
            // p is invalid
            p[0] = 0;
            await AssertValidationError(["LNURLW"], () => client.RegisterBoltcard(test4.Id, new RegisterBoltcardRequest()
            {
                OnExisting = OnExistingBehavior.KeepVersion,
                LNURLW = card2.LNURLW + $"?p={Encoders.Hex.EncodeData(AESKey.Parse(card2.K1).Encrypt(p))}"
            }));
            // Test with SATS denomination values
            var testSats = await client.CreatePullPayment(storeId, new Client.Models.CreatePullPaymentRequest()
            {
                Name = "Test SATS",
                Amount = 21000,
                Currency = "SATS",
                PayoutMethods = new[] { "BTC", "BTC-LightningNetwork", "BTC_LightningLike" }
            });
            lnrURLs = await unauthenticated.GetPullPaymentLNURL(testSats.Id);
            Assert.IsType<string>(lnrURLs.LNURLBech32);
            Assert.IsType<string>(lnrURLs.LNURLUri);
            Assert.Equal(21000, testSats.Amount);
            Assert.Equal("SATS", testSats.Currency);

            //permission test around auto approved pps and payouts
            var nonApproved = await acc.CreateClient(Policies.CanCreateNonApprovedPullPayments);
            var approved = await acc.CreateClient(Policies.CanCreatePullPayments);
            await AssertPermissionError(Policies.CanCreatePullPayments, async () =>
            {
                await nonApproved.CreatePullPayment(acc.StoreId, new CreatePullPaymentRequest()
                {
                    Amount = 100,
                    Currency = "USD",
                    Name = "pull payment",
                    PayoutMethods = new[] { "BTC" },
                    AutoApproveClaims = true
                });
            });
            await AssertPermissionError(Policies.CanCreatePullPayments, async () =>
            {
                await nonApproved.CreatePayout(acc.StoreId, new CreatePayoutThroughStoreRequest()
                {
                    Amount = 100,
                    PayoutMethodId = "BTC",
                    Approved = true,
                    Destination = new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString()
                });
            });

            await approved.CreatePullPayment(acc.StoreId, new CreatePullPaymentRequest()
            {
                Amount = 100,
                Currency = "USD",
                Name = "pull payment",
                PayoutMethods = new[] { "BTC" },
                AutoApproveClaims = true
            });

            await approved.CreatePayout(acc.StoreId, new CreatePayoutThroughStoreRequest()
            {
                Amount = 100,
                PayoutMethodId = "BTC",
                Approved = true,
                Destination = new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString()
            });
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanProcessPayoutsExternally()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var acc = tester.NewAccount();
            acc.Register();
            await acc.CreateStoreAsync();
            var storeId = (await acc.RegisterDerivationSchemeAsync("BTC", importKeysToNBX: true)).StoreId;
            var client = await acc.CreateClient();
            var address = await tester.ExplorerNode.GetNewAddressAsync();
            var payout = await client.CreatePayout(storeId, new CreatePayoutThroughStoreRequest()
            {
                Approved = false,
                PayoutMethodId = "BTC",
                Amount = 0.0001m,
                Destination = address.ToString(),

            });
            await AssertAPIError("invalid-state", async () =>
            {
                await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest() { State = PayoutState.Completed });

            });

            await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());

            await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest() { State = PayoutState.Completed });
            Assert.Equal(PayoutState.Completed, (await client.GetStorePayouts(storeId, false)).Single(data => data.Id == payout.Id).State);
            Assert.Null((await client.GetStorePayouts(storeId, false)).Single(data => data.Id == payout.Id).PaymentProof);

            foreach (var state in new[] { PayoutState.AwaitingApproval, PayoutState.Cancelled, PayoutState.Completed, PayoutState.AwaitingApproval, PayoutState.InProgress })
            {
                await AssertAPIError("invalid-state", async () =>
                {
                    await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest() { State = state });
                });
            }
            payout = await client.CreatePayout(storeId, new CreatePayoutThroughStoreRequest()
            {
                Approved = true,
                PayoutMethodId = "BTC",
                Amount = 0.0001m,
                Destination = address.ToString()
            });

            payout = await client.GetStorePayout(storeId, payout.Id);
            Assert.NotNull(payout);
            Assert.Equal(PayoutState.AwaitingPayment, payout.State);
            await AssertValidationError(new[] { "PaymentProof" }, async () =>
              {
                  await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest()
                  {
                      State = PayoutState.Completed,
                      PaymentProof = JObject.FromObject(new
                      {
                          test = "zyx"
                      })
                  });
              });
            await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest()
            {
                State = PayoutState.InProgress,
                PaymentProof = JObject.FromObject(new
                {
                    proofType = "external-proof"
                })
            });
            payout = await client.GetStorePayout(storeId, payout.Id);
            Assert.NotNull(payout);
            Assert.Equal(PayoutState.InProgress, payout.State);
            Assert.True(payout.PaymentProof.TryGetValue("proofType", out var savedType));
            Assert.Equal("external-proof", savedType);

            await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest()
            {
                State = PayoutState.AwaitingPayment,
                PaymentProof = JObject.FromObject(new
                {
                    proofType = "external-proof",
                    id = "finality proof",
                    link = "proof.com"
                })
            });
            payout = await client.GetStorePayout(storeId, payout.Id);
            Assert.NotNull(payout);
            Assert.Null(payout.PaymentProof);
            Assert.Equal(PayoutState.AwaitingPayment, payout.State);

            await client.MarkPayout(storeId, payout.Id, new MarkPayoutRequest()
            {
                State = PayoutState.Completed,
                PaymentProof = JObject.FromObject(new
                {
                    proofType = "external-proof",
                    id = "finality proof",
                    link = "proof.com"
                })
            });
            payout = await client.GetStorePayout(storeId, payout.Id);
            Assert.NotNull(payout);
            Assert.Equal(PayoutState.Completed, payout.State);
            Assert.True(payout.PaymentProof.TryGetValue("proofType", out savedType));
            Assert.True(payout.PaymentProof.TryGetValue("link", out var savedLink));
            Assert.True(payout.PaymentProof.TryGetValue("id", out var savedId));
            Assert.Equal("external-proof", savedType);
            Assert.Equal("finality proof", savedId);
            Assert.Equal("proof.com", savedLink);
        }

        private DateTimeOffset RoundSeconds(DateTimeOffset dateTimeOffset)
        {
            return new DateTimeOffset(dateTimeOffset.Year, dateTimeOffset.Month, dateTimeOffset.Day, dateTimeOffset.Hour, dateTimeOffset.Minute, dateTimeOffset.Second, dateTimeOffset.Offset);
        }

        private async Task<GreenfieldAPIException> AssertAPIError(string expectedError, Func<Task> act)
        {
            var err = await Assert.ThrowsAsync<GreenfieldAPIException>(async () => await act());
            Assert.Equal(expectedError, err.APIError.Code);
            return err;
        }
        private async Task<GreenfieldAPIException> AssertPermissionError(string expectedPermission, Func<Task> act)
        {
            var err = await Assert.ThrowsAsync<GreenfieldAPIException>(async () => await act());
            var err2 = Assert.IsType<GreenfieldPermissionAPIError>(err.APIError);
            Assert.Equal(expectedPermission, err2.MissingPermission);
            return err;
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task StoresControllerTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.GrantAccessAsync();
            await user.MakeAdmin();
            var client = await user.CreateClient(Policies.Unrestricted);

            //create store
            var newStore = await client.CreateStore(new CreateStoreRequest { Name = "A" });
            Assert.Equal("A", newStore.Name);

            // validate
            await AssertValidationError(["CssUrl", "LogoUrl", "BrandColor"], async () =>
                await client.UpdateStore(newStore.Id, new UpdateStoreRequest
                {
                    CssUrl = "style.css",
                    LogoUrl = "logo.svg",
                    BrandColor = "invalid"
                }));

            //update store
            Assert.Empty(newStore.PaymentMethodCriteria);
            await client.GenerateOnChainWallet(newStore.Id, "BTC", new GenerateOnChainWalletRequest());
            var updatedStore = await client.UpdateStore(newStore.Id, new UpdateStoreRequest
            {
                Name = "B",
                CssUrl = "https://example.org/style.css",
                LogoUrl = "https://example.org/logo.svg",
                BrandColor = "#003366",
                ApplyBrandColorToBackend = true,
                PaymentMethodCriteria = new List<PaymentMethodCriteriaData>
            {
                new()
                {
                    Amount = 10,
                    Above = true,
                    PaymentMethodId = "BTC",
                    CurrencyCode = "USD"
                }
             }
            });
            Assert.Equal("B", updatedStore.Name);
            Assert.Equal("https://example.org/style.css", updatedStore.CssUrl);
            Assert.Equal("https://example.org/logo.svg", updatedStore.LogoUrl);
            Assert.Equal("#003366", updatedStore.BrandColor);
            Assert.True(updatedStore.ApplyBrandColorToBackend);
            var s = (await client.GetStore(newStore.Id));
            Assert.Equal("B", s.Name);
            var pmc = Assert.Single(s.PaymentMethodCriteria);
            //check that pmc equals the one we set
            Assert.Equal(10, pmc.Amount);
            Assert.True(pmc.Above);
            Assert.Equal("BTC-CHAIN", pmc.PaymentMethodId);
            Assert.Equal("USD", pmc.CurrencyCode);
            updatedStore = await client.UpdateStore(newStore.Id, new UpdateStoreRequest() { Name = "B" });
            Assert.Empty(newStore.PaymentMethodCriteria);

            //list stores
            var stores = await client.GetStores();
            var storeIds = stores.Select(data => data.Id);
            var storeNames = stores.Select(data => data.Name);
            Assert.NotNull(stores);
            Assert.Equal(2, stores.Count());
            Assert.Contains(newStore.Id, storeIds);
            Assert.Contains(user.StoreId, storeIds);

            //get store
            var store = await client.GetStore(user.StoreId);
            Assert.Equal(user.StoreId, store.Id);
            Assert.Contains(store.Name, storeNames);

            //remove store
            await client.RemoveStore(newStore.Id);
            await AssertHttpError(403, async () =>
            {
                await client.GetStore(newStore.Id);
            });
            Assert.Single(await client.GetStores());

            newStore = await client.CreateStore(new CreateStoreRequest() { Name = "A" });
            var scopedClient =
                await user.CreateClient(Permission.Create(Policies.CanViewStoreSettings, user.StoreId).ToString());
            Assert.Single(await scopedClient.GetStores());

            // We strip the user's Owner right, so the key should not work
            using var ctx = tester.PayTester.GetService<Data.ApplicationDbContextFactory>().CreateContext();
            var storeEntity = await ctx.UserStore.SingleAsync(u => u.ApplicationUserId == user.UserId && u.StoreDataId == newStore.Id);
            var roleId = (await tester.PayTester.GetService<StoreRepository>().GetStoreRoles(null)).Single(r => r.Role == "Guest").Id;
            storeEntity.StoreRoleId = roleId;
            await ctx.SaveChangesAsync();
            await AssertHttpError(403, async () => await client.UpdateStore(newStore.Id, new UpdateStoreRequest() { Name = "B" }));

            client = await user.CreateClient(Policies.Unrestricted);
            stores = await client.GetStores();
            foreach (var s2 in stores)
            {
                await tester.PayTester.StoreRepository.DeleteStore(s2.Id);
            }
            tester.DeleteStore = false;
            Assert.Empty(await client.GetStores());

            // Archive
            var archivableStore = await client.CreateStore(new CreateStoreRequest { Name = "Archivable" });
            Assert.False(archivableStore.Archived);
            archivableStore = await client.UpdateStore(archivableStore.Id, new UpdateStoreRequest { Name = "Archived", Archived = true });
            Assert.Equal("Archived", archivableStore.Name);
            Assert.True(archivableStore.Archived);
        }

        private async Task<GreenfieldValidationException> AssertValidationError(string[] fields, Func<Task> act)
        {
            var remainingFields = fields.ToHashSet();
            var ex = await Assert.ThrowsAsync<GreenfieldValidationException>(act);
            foreach (var field in fields)
            {
                Assert.Contains(field, ex.ValidationErrors.Select(e => e.Path).ToArray());
                remainingFields.Remove(field);
            }
            Assert.Empty(remainingFields);
            return ex;
        }

        private async Task AssertHttpError(int code, Func<Task> act)
        {
            var ex = await Assert.ThrowsAsync<GreenfieldAPIException>(act);
            Assert.Equal(code, ex.HttpCode);
        }

        private async Task AssertApiError(int httpStatus, string errorCode, Func<Task> act)
        {
            var ex = await Assert.ThrowsAsync<GreenfieldAPIException>(act);
            Assert.Equal(httpStatus, ex.HttpCode);
            Assert.Equal(errorCode, ex.APIError.Code);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task UsersControllerTests()
        {
            using var tester = CreateServerTester(newDb: true);
            tester.PayTester.DisableRegistration = true;
            await tester.StartAsync();
            var user = tester.NewAccount();
            user.GrantAccess();
            await user.MakeAdmin();
            var clientProfile = await user.CreateClient(Policies.CanModifyProfile);
            var clientServer = await user.CreateClient(Policies.CanCreateUser, Policies.CanViewProfile);
            var clientInsufficient = await user.CreateClient(Policies.CanModifyStoreSettings);
            var clientBasic = await user.CreateClient();


            var apiKeyProfileUserData = await clientProfile.GetCurrentUser();
            Assert.NotNull(apiKeyProfileUserData);
            Assert.Equal(apiKeyProfileUserData.Id, user.UserId);
            Assert.Equal(apiKeyProfileUserData.Email, user.RegisterDetails.Email);
            Assert.Contains("ServerAdmin", apiKeyProfileUserData.Roles);

            await AssertHttpError(403, async () => await clientInsufficient.GetCurrentUser());
            await clientServer.GetCurrentUser();
            await clientProfile.GetCurrentUser();
            await clientBasic.GetCurrentUser();

            await AssertHttpError(403, async () =>
                await clientInsufficient.CreateUser(new CreateApplicationUserRequest()
                {
                    Email = $"{Guid.NewGuid()}@g.com",
                    Password = Guid.NewGuid().ToString()
                }));

            var newUser = await clientServer.CreateUser(new CreateApplicationUserRequest()
            {
                Email = $"{Guid.NewGuid()}@g.com",
                Password = Guid.NewGuid().ToString()
            });
            Assert.NotNull(newUser);

            var newUser2 = await clientBasic.CreateUser(new CreateApplicationUserRequest()
            {
                Email = $"{Guid.NewGuid()}@g.com",
                Password = Guid.NewGuid().ToString()
            });
            Assert.NotNull(newUser2);

            await AssertValidationError(new[] { "Email" }, async () =>
                await clientServer.CreateUser(new CreateApplicationUserRequest()
                {
                    Email = $"{Guid.NewGuid()}",
                    Password = Guid.NewGuid().ToString()
                }));

            await AssertValidationError(new[] { "Email" }, async () =>
                await clientServer.CreateUser(
                    new CreateApplicationUserRequest() { Password = Guid.NewGuid().ToString() }));
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanUseWebhooks()
        {
            void AssertHook(FakeServer fakeServer, StoreWebhookData hook)
            {
                Assert.True(hook.Enabled);
                Assert.True(hook.AuthorizedEvents.Everything);
                Assert.False(hook.AutomaticRedelivery);
                Assert.Equal(fakeServer.ServerUri.AbsoluteUri, hook.Url);
            }
            using var tester = CreateServerTester(newDb: true);
            using var fakeServer = new FakeServer();
            await fakeServer.Start();
            await tester.StartAsync();
            var user = tester.NewAccount();
            user.GrantAccess();
            user.RegisterDerivationScheme("BTC");
            var clientProfile = await user.CreateClient(Policies.CanModifyWebhooks, Policies.CanCreateInvoice);
            var hook = await clientProfile.CreateWebhook(user.StoreId, new CreateStoreWebhookRequest()
            {
                Url = fakeServer.ServerUri.AbsoluteUri,
                AutomaticRedelivery = false
            });
            Assert.NotNull(hook.Secret);
            AssertHook(fakeServer, hook);
            hook = await clientProfile.GetWebhook(user.StoreId, hook.Id);
            AssertHook(fakeServer, hook);
            var hooks = await clientProfile.GetWebhooks(user.StoreId);
            hook = Assert.Single(hooks);
            AssertHook(fakeServer, hook);
            await clientProfile.CreateInvoice(user.StoreId,
                        new CreateInvoiceRequest() { Currency = "USD", Amount = 100 });
            var req = await fakeServer.GetNextRequest();
            req.Response.StatusCode = 200;
            fakeServer.Done();
            hook = await clientProfile.UpdateWebhook(user.StoreId, hook.Id, new UpdateStoreWebhookRequest()
            {
                Url = hook.Url,
                Secret = "lol",
                AutomaticRedelivery = false
            });
            Assert.Null(hook.Secret);
            AssertHook(fakeServer, hook);
            WebhookDeliveryData delivery = null;
            await TestUtils.EventuallyAsync(async () =>
            {
                var deliveries = await clientProfile.GetWebhookDeliveries(user.StoreId, hook.Id);
                delivery = Assert.Single(deliveries);
            });

            delivery = await clientProfile.GetWebhookDelivery(user.StoreId, hook.Id, delivery.Id);
            Assert.NotNull(delivery);
            Assert.Equal(WebhookDeliveryStatus.HttpSuccess, delivery.Status);

            var newDeliveryId = await clientProfile.RedeliverWebhook(user.StoreId, hook.Id, delivery.Id);
            req = await fakeServer.GetNextRequest();
            req.Response.StatusCode = 404;
            Assert.StartsWith("BTCPayServer", Assert.Single(req.Request.Headers.UserAgent));
            await TestUtils.EventuallyAsync(async () =>
            {
                // Releasing semaphore several times may help making this test less flaky
                fakeServer.Done();
                var newDelivery = await clientProfile.GetWebhookDelivery(user.StoreId, hook.Id, newDeliveryId);
                Assert.NotNull(newDelivery);
                Assert.Equal(404, newDelivery.HttpCode);
                var req = await clientProfile.GetWebhookDeliveryRequest(user.StoreId, hook.Id, newDeliveryId);
                Assert.Equal(delivery.Id, req.OriginalDeliveryId);
                Assert.True(req.IsRedelivery);
                Assert.Equal(WebhookDeliveryStatus.HttpError, newDelivery.Status);
            });
            var deliveries = await clientProfile.GetWebhookDeliveries(user.StoreId, hook.Id);
            Assert.Equal(2, deliveries.Length);
            Assert.Equal(newDeliveryId, deliveries[0].Id);
            var jObj = await clientProfile.GetWebhookDeliveryRequest(user.StoreId, hook.Id, newDeliveryId);
            Assert.NotNull(jObj);

            TestLogs.LogInformation("Should not be able to access webhook without proper auth");
            var unauthorized = await user.CreateClient(Policies.CanCreateInvoice);
            await AssertHttpError(403, async () =>
            {
                await unauthorized.GetWebhookDeliveryRequest(user.StoreId, hook.Id, newDeliveryId);
            });

            TestLogs.LogInformation("Can use btcpay.store.canmodifystoresettings to query webhooks");
            clientProfile = await user.CreateClient(Policies.CanModifyStoreSettings, Policies.CanCreateInvoice);
            await clientProfile.GetWebhookDeliveryRequest(user.StoreId, hook.Id, newDeliveryId);


            TestLogs.LogInformation("Can prune deliveries");
            var cleanup = tester.PayTester.GetService<HostedServices.CleanupWebhookDeliveriesTask>();
            cleanup.BatchSize = 1;
            cleanup.PruneAfter = TimeSpan.Zero;
            await cleanup.Do(default);
            await AssertHttpError(409, () => clientProfile.RedeliverWebhook(user.StoreId, hook.Id, delivery.Id));

            TestLogs.LogInformation("Testing corner cases");
            Assert.Null(await clientProfile.GetWebhookDeliveryRequest(user.StoreId, "lol", newDeliveryId));
            Assert.Null(await clientProfile.GetWebhookDeliveryRequest(user.StoreId, hook.Id, "lol"));
            Assert.Null(await clientProfile.GetWebhookDeliveryRequest(user.StoreId, "lol", "lol"));
            Assert.Null(await clientProfile.GetWebhook(user.StoreId, "lol"));
            await AssertHttpError(404, async () =>
            {
                await clientProfile.UpdateWebhook(user.StoreId, "lol", new UpdateStoreWebhookRequest() { Url = hook.Url });
            });

            Assert.True(await clientProfile.DeleteWebhook(user.StoreId, hook.Id));
            Assert.False(await clientProfile.DeleteWebhook(user.StoreId, hook.Id));
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task HealthControllerTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);

            var apiHealthData = await unauthClient.GetHealth();
            Assert.NotNull(apiHealthData);
            Assert.True(apiHealthData.Synchronized);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task ServerInfoControllerTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);
            await AssertHttpError(401, async () => await unauthClient.GetServerInfo());

            var user = tester.NewAccount();
            user.GrantAccess();
            var clientBasic = await user.CreateClient();
            var serverInfoData = await clientBasic.GetServerInfo();
            Assert.NotNull(serverInfoData);
            Assert.NotNull(serverInfoData.Version);
            Assert.NotNull(serverInfoData.Onion);
            Assert.True(serverInfoData.FullySynched);
            Assert.Contains("BTC-CHAIN", serverInfoData.SupportedPaymentMethods);
            Assert.Contains("BTC-LN", serverInfoData.SupportedPaymentMethods);
            Assert.NotNull(serverInfoData.SyncStatus);
            Assert.Single(serverInfoData.SyncStatus.Select(s => s.PaymentMethodId == "BTC-CHAIN"));
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task PaymentControllerTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.GrantAccessAsync();
            await user.MakeAdmin();
            var client = await user.CreateClient(Policies.Unrestricted);
            var viewOnly = await user.CreateClient(Policies.CanViewPaymentRequests);

            //create payment request

            //validation errors
            await AssertValidationError(new[] { "Amount" }, async () =>
            {
                await client.CreatePaymentRequest(user.StoreId, new() { Title = "A" });
            });
            await AssertValidationError(new[] { "Amount" }, async () =>
            {
                await client.CreatePaymentRequest(user.StoreId,
                    new() { Title = "A", Currency = "BTC", Amount = 0 });
            });
            await AssertValidationError(new[] { "Currency" }, async () =>
            {
                await client.CreatePaymentRequest(user.StoreId,
                    new() { Title = "A", Currency = "helloinvalid", Amount = 1 });
            });
            await AssertHttpError(403, async () =>
            {
                await viewOnly.CreatePaymentRequest(user.StoreId,
                    new() { Title = "A", Currency = "helloinvalid", Amount = 1 });
            });
            var newPaymentRequest = await client.CreatePaymentRequest(user.StoreId,
                new() { Title = "A", Currency = "USD", Amount = 1, ReferenceId = "1234"});

            //list payment request
            var paymentRequests = await viewOnly.GetPaymentRequests(user.StoreId);

            Assert.NotNull(paymentRequests);
            Assert.Single(paymentRequests);
            Assert.Equal(newPaymentRequest.Id, paymentRequests.First().Id);

            //get payment request
            var paymentRequest = await viewOnly.GetPaymentRequest(user.StoreId, newPaymentRequest.Id);
            Assert.Equal(newPaymentRequest.Title, paymentRequest.Title);
            Assert.Equal(newPaymentRequest.StoreId, user.StoreId);
            Assert.Equal(newPaymentRequest.ReferenceId, paymentRequest.ReferenceId);

            //update payment request
            var updateRequest = paymentRequest;
            updateRequest.Title = "B";
            updateRequest.ReferenceId = "EmperorNicolasGeneralRockstar";
            await AssertHttpError(403, async () =>
            {
                await viewOnly.UpdatePaymentRequest(user.StoreId, paymentRequest.Id, updateRequest);
            });
            await client.UpdatePaymentRequest(user.StoreId, paymentRequest.Id, updateRequest);
            paymentRequest = await client.GetPaymentRequest(user.StoreId, newPaymentRequest.Id);
            Assert.Equal(updateRequest.Title, paymentRequest.Title);
            Assert.Equal(updateRequest.ReferenceId, paymentRequest.ReferenceId);

            //archive payment request
            await AssertHttpError(403, async () =>
            {
                await viewOnly.ArchivePaymentRequest(user.StoreId, paymentRequest.Id);
            });

            await client.ArchivePaymentRequest(user.StoreId, paymentRequest.Id);
            Assert.DoesNotContain(paymentRequest.Id,
                (await client.GetPaymentRequests(user.StoreId)).Select(data => data.Id));
            var archivedPrId = paymentRequest.Id;
            //let's test some payment stuff with the UI
            await user.RegisterDerivationSchemeAsync("BTC");
            var paymentTestPaymentRequest = await client.CreatePaymentRequest(user.StoreId,
                new() { Amount = 0.1m, Currency = "BTC", Title = "Payment test title" });

            var invoiceId = Assert.IsType<string>(Assert.IsType<OkObjectResult>(await user.GetController<UIPaymentRequestController>()
                .PayPaymentRequest(paymentTestPaymentRequest.Id, false)).Value);

            async Task Pay(string invoiceId, bool partialPayment = false)
            {
                TestLogs.LogInformation($"Paying invoice {invoiceId}");
                var invoice = user.BitPay.GetInvoice(invoiceId);
                await tester.WaitForEvent<InvoiceDataChangedEvent>(async () =>
                {
                    TestLogs.LogInformation($"Paying address {invoice.BitcoinAddress}");
                    await tester.ExplorerNode.SendToAddressAsync(
                        BitcoinAddress.Create(invoice.BitcoinAddress, tester.ExplorerNode.Network), invoice.BtcDue);
                });
                await TestUtils.EventuallyAsync(async () =>
                {
                    Assert.Equal(Invoice.STATUS_PAID, (await user.BitPay.GetInvoiceAsync(invoiceId)).Status);
                    if (!partialPayment)
                        Assert.Equal(PaymentRequestStatus.Processing, (await client.GetPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id)).Status);
                });
                await tester.ExplorerNode.GenerateAsync(1);
                await TestUtils.EventuallyAsync(async () =>
                {
                    Assert.Equal(Invoice.STATUS_COMPLETE, (await user.BitPay.GetInvoiceAsync(invoiceId)).Status);
                    if (!partialPayment)
                        Assert.Equal(PaymentRequestStatus.Completed, (await client.GetPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id)).Status);
                });
            }
            await Pay(invoiceId);

            //Same thing, but with the API
            paymentTestPaymentRequest = await client.CreatePaymentRequest(user.StoreId,
                new() { Amount = 0.1m, Currency = "BTC", Title = "Payment test title" });
            var paidPrId = paymentTestPaymentRequest.Id;
            var invoiceData = await client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest());
            await Pay(invoiceData.Id);

            // Can't update amount once invoice has been created
            await AssertValidationError(new[] { "Amount" }, () => client.UpdatePaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new()
            {
                Amount = 294m
            }));

            // Let's tests some unhappy path
            paymentTestPaymentRequest = await client.CreatePaymentRequest(user.StoreId,
                new() { Amount = 0.1m, AllowCustomPaymentAmounts = false, Currency = "BTC", Title = "Payment test title" });
            await AssertValidationError(new[] { "Amount" }, () => client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest() { Amount = -0.04m }));
            await AssertValidationError(new[] { "Amount" }, () => client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest() { Amount = 0.04m }));
            await client.UpdatePaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new()
            {
                Amount = 0.1m,
                AllowCustomPaymentAmounts = true,
                Currency = "BTC",
                Title = "Payment test title"
            });
            await AssertValidationError(new[] { "Amount" }, () => client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest() { Amount = -0.04m }));
            invoiceData = await client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest() { Amount = 0.04m });
            Assert.Equal(0.04m, invoiceData.Amount);
            var firstPaymentId = invoiceData.Id;
            await AssertAPIError("archived", () => client.PayPaymentRequest(user.StoreId, archivedPrId, new PayPaymentRequestRequest()));

            await client.UpdatePaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new()
            {
                Amount = 0.1m,
                AllowCustomPaymentAmounts = true,
                Currency = "BTC",
                Title = "Payment test title",
                ExpiryDate = DateTimeOffset.UtcNow - TimeSpan.FromDays(1.0)
            });

            await AssertAPIError("expired", () => client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest()));
            await AssertAPIError("already-paid", () => client.PayPaymentRequest(user.StoreId, paidPrId, new PayPaymentRequestRequest()));

            await client.UpdatePaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new()
            {
                Amount = 0.1m,
                AllowCustomPaymentAmounts = true,
                Currency = "BTC",
                Title = "Payment test title",
                ExpiryDate = null
            });

            await Pay(firstPaymentId, true);
            invoiceData = await client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest());

            Assert.Equal(0.06m, invoiceData.Amount);
            Assert.Equal("BTC", invoiceData.Currency);

            var expectedInvoiceId = invoiceData.Id;
            invoiceData = await client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest() { AllowPendingInvoiceReuse = true });
            Assert.Equal(expectedInvoiceId, invoiceData.Id);

            var notExpectedInvoiceId = invoiceData.Id;
            invoiceData = await client.PayPaymentRequest(user.StoreId, paymentTestPaymentRequest.Id, new PayPaymentRequestRequest() { AllowPendingInvoiceReuse = false });
            Assert.NotEqual(notExpectedInvoiceId, invoiceData.Id);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task InvoiceLegacyTests()
        {
            using (var tester = CreateServerTester())
            {
                await tester.StartAsync();
                var user = tester.NewAccount();
                await user.GrantAccessAsync();
                user.RegisterDerivationScheme("BTC");
                var client = await user.CreateClient(Policies.Unrestricted);
                var oldBitpay = user.BitPay;

                TestLogs.LogInformation("Let's create an invoice with bitpay API");
                var oldInvoice = await oldBitpay.CreateInvoiceAsync(new Invoice()
                {
                    Currency = "BTC",
                    Price = 1000.19392922m,
                    BuyerAddress1 = "blah",
                    Buyer = new Buyer()
                    {
                        Address2 = "blah2"
                    },
                    ItemCode = "code",
                    ItemDesc = "desc",
                    OrderId = "orderId",
                    PosData = "posData"
                });

                async Task<Client.Models.InvoiceData> AssertInvoiceMetadata()
                {
                    TestLogs.LogInformation("Let's check if we can get invoice in the new format with the metadata");
                    var newInvoice = await client.GetInvoice(user.StoreId, oldInvoice.Id);
                    Assert.Equal("posData", newInvoice.Metadata["posData"].Value<string>());
                    Assert.Equal("code", newInvoice.Metadata["itemCode"].Value<string>());
                    Assert.Equal("desc", newInvoice.Metadata["itemDesc"].Value<string>());
                    Assert.Equal("orderId", newInvoice.Metadata["orderId"].Value<string>());
                    Assert.False(newInvoice.Metadata["physical"].Value<bool>());
                    Assert.Null(newInvoice.Metadata["buyerCountry"]);
                    Assert.Equal(1000.19392922m, newInvoice.Amount);
                    Assert.Equal("BTC", newInvoice.Currency);
                    return newInvoice;
                }

                await AssertInvoiceMetadata();
                TestLogs.LogInformation("Let's hack the Bitpay created invoice to be just like before this update. (Invoice V1)");
                var invoiceV1 = "{\r\n  \"version\": 1,\r\n  \"id\": \"" + oldInvoice.Id + "\",\r\n  \"storeId\": \"" + user.StoreId + "\",\r\n  \"orderId\": \"orderId\",\r\n  \"speedPolicy\": 1,\r\n  \"rate\": 1.0,\r\n  \"invoiceTime\": 1598329634,\r\n  \"expirationTime\": 1598330534,\r\n  \"depositAddress\": \"mm83rVs8ZnZok1SkRBmXiwQSiPFgTgCKpD\",\r\n  \"productInformation\": {\r\n    \"itemDesc\": \"desc\",\r\n    \"itemCode\": \"code\",\r\n    \"physical\": false,\r\n    \"price\": 1000.19392922,\r\n    \"currency\": \"BTC\"\r\n  },\r\n  \"buyerInformation\": {\r\n    \"buyerName\": null,\r\n    \"buyerEmail\": null,\r\n    \"buyerCountry\": null,\r\n    \"buyerZip\": null,\r\n    \"buyerState\": null,\r\n    \"buyerCity\": null,\r\n    \"buyerAddress2\": \"blah2\",\r\n    \"buyerAddress1\": \"blah\",\r\n    \"buyerPhone\": null\r\n  },\r\n  \"posData\": \"posData\",\r\n  \"internalTags\": [],\r\n  \"derivationStrategy\": null,\r\n  \"derivationStrategies\": \"{\\\"BTC\\\":{\\\"signingKey\\\":\\\"tpubDD1AW2ruUxSsDa55NQYtNt7DQw9bqXx4K7r2aScySmjxHtsCZoxFTN3qCMcKLxgsRDMGSwk9qj1fBfi8jqSLenwyYkhDrmgaxQuvuKrTHEf\\\",\\\"source\\\":\\\"NBXplorer\\\",\\\"accountDerivation\\\":\\\"tpubDD1AW2ruUxSsDa55NQYtNt7DQw9bqXx4K7r2aScySmjxHtsCZoxFTN3qCMcKLxgsRDMGSwk9qj1fBfi8jqSLenwyYkhDrmgaxQuvuKrTHEf-[legacy]\\\",\\\"accountOriginal\\\":null,\\\"accountKeySettings\\\":[{\\\"rootFingerprint\\\":\\\"54d5044d\\\",\\\"accountKeyPath\\\":\\\"44'/1'/0'\\\",\\\"accountKey\\\":\\\"tpubDD1AW2ruUxSsDa55NQYtNt7DQw9bqXx4K7r2aScySmjxHtsCZoxFTN3qCMcKLxgsRDMGSwk9qj1fBfi8jqSLenwyYkhDrmgaxQuvuKrTHEf\\\"}],\\\"label\\\":null}}\",\r\n  \"status\": \"new\",\r\n  \"exceptionStatus\": \"\",\r\n  \"payments\": [],\r\n  \"refundable\": false,\r\n  \"refundMail\": null,\r\n  \"redirectURL\": null,\r\n  \"redirectAutomatically\": false,\r\n  \"txFee\": 0,\r\n  \"fullNotifications\": false,\r\n  \"notificationEmail\": null,\r\n  \"notificationURL\": null,\r\n  \"serverUrl\": \"http://127.0.0.1:8001\",\r\n  \"cryptoData\": {\r\n    \"BTC\": {\r\n      \"rate\": 1.0,\r\n      \"paymentMethod\": {\r\n        \"networkFeeMode\": 0,\r\n        \"networkFeeRate\": 100.0,\r\n        \"payjoinEnabled\": false\r\n      },\r\n      \"feeRate\": 100.0,\r\n      \"txFee\": 0,\r\n      \"depositAddress\": \"mm83rVs8ZnZok1SkRBmXiwQSiPFgTgCKpD\"\r\n    }\r\n  },\r\n  \"monitoringExpiration\": 1598416934,\r\n  \"historicalAddresses\": null,\r\n  \"availableAddressHashes\": null,\r\n  \"extendedNotifications\": false,\r\n  \"events\": null,\r\n  \"paymentTolerance\": 0.0,\r\n  \"archived\": false\r\n}";
                var db = tester.PayTester.GetService<Data.ApplicationDbContextFactory>();
                using var ctx = db.CreateContext();
                var dbInvoice = await ctx.Invoices.FindAsync(oldInvoice.Id);
#pragma warning disable CS0618 // Type or member is obsolete
                dbInvoice.Blob = ZipUtils.Zip(invoiceV1);
#pragma warning restore CS0618 // Type or member is obsolete
                await ctx.SaveChangesAsync();
                var newInvoice = await AssertInvoiceMetadata();

                TestLogs.LogInformation("Now, let's create an invoice with the new API but with the same metadata as Bitpay");
                newInvoice.Metadata.Add("lol", "lol");
                newInvoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest()
                {
                    Metadata = newInvoice.Metadata,
                    Amount = 1000.19392922m,
                    Currency = "BTC"
                });
                oldInvoice = await oldBitpay.GetInvoiceAsync(newInvoice.Id);
                await AssertInvoiceMetadata();
                Assert.Equal("lol", newInvoice.Metadata["lol"].Value<string>());
            }
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanOverpayInvoice()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.RegisterDerivationSchemeAsync("BTC");
            var client = await user.CreateClient();
            var invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest() { Amount = 5000.0m, Currency = "USD" });
            var methods = await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id);
            var method = methods.First();
            var amount = method.Amount;
            Assert.Equal(amount, method.Due);
#pragma warning disable CS0618 // Type or member is obsolete
            var btc = tester.NetworkProvider.BTC.NBitcoinNetwork;
#pragma warning restore CS0618 // Type or member is obsolete
            await tester.ExplorerNode.SendToAddressAsync(BitcoinAddress.Create(method.Destination, btc), Money.Coins(method.Due) + Money.Coins(1.0m));
            await TestUtils.EventuallyAsync(async () =>
            {
                invoice = await client.GetInvoice(user.StoreId, invoice.Id);
                Assert.True(invoice.Status == InvoiceStatus.Processing);
                methods = await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id);
                method = methods.First();
                Assert.Equal(amount, method.Amount);
                Assert.Equal(-1.0m, method.Due);
                Assert.Equal(amount + 1.0m, method.TotalPaid);
            });
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task CanRefundInvoice()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.RegisterDerivationSchemeAsync("BTC");
            var client = await user.CreateClient();
            var store = await client.GetStore(user.StoreId);
            Assert.Equal(TimeSpan.FromDays(30.0), store.RefundBOLT11Expiration);
            store.RefundBOLT11Expiration = TimeSpan.FromDays(1);
            await client.UpdateStore(store.Id, store);
            store = await client.GetStore(user.StoreId);
            Assert.Equal(TimeSpan.FromDays(1.0), store.RefundBOLT11Expiration);

            var invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest() { Amount = 5000.0m, Currency = "USD" });
            var methods = await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id);
            var method = methods.First();
            var amount = method.Amount;
            Assert.Equal(amount, method.Due);

            await tester.WaitForEvent<NewOnChainTransactionEvent>(async () =>
            {
                await tester.ExplorerNode.SendToAddressAsync(
                    BitcoinAddress.Create(method.Destination, tester.NetworkProvider.BTC.NBitcoinNetwork),
                    Money.Coins(method.Due)
                );
            });

            // test validation that the invoice exists
            await AssertHttpError(404, async () =>
            {
                await client.RefundInvoice(user.StoreId, "lol fake invoice id", new RefundInvoiceRequest()
                {
                    PayoutMethodId = method.PaymentMethodId,
                    RefundVariant = RefundVariant.RateThen
                });
            });

            // test validation error for when invoice is not yet in the state in which it can be refunded
            var apiError = await AssertAPIError("non-refundable", () => client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.RateThen
            }));
            Assert.Equal("Cannot refund this invoice", apiError.Message);

            await TestUtils.EventuallyAsync(async () =>
            {
                invoice = await client.GetInvoice(user.StoreId, invoice.Id);
                Assert.True(invoice.Status == InvoiceStatus.Processing);
            });

            // need to set the status to the one in which we can actually refund the invoice
            await client.MarkInvoiceStatus(user.StoreId, invoice.Id, new MarkInvoiceStatusRequest()
            {
                Status = InvoiceStatus.Settled
            });

            // test validation for the payment method
            var validationError = await AssertValidationError(new[] { "PayoutMethodId" }, async () =>
            {
                await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
                {
                    PayoutMethodId = "fake payment method",
                    RefundVariant = RefundVariant.RateThen
                });
            });
            Assert.Contains("PayoutMethodId: Please select one of the payment methods which were available for the original invoice", validationError.Message);

            // test RefundVariant.RateThen
            var pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.RateThen
            });
            Assert.Equal(pp.BOLT11Expiration, TimeSpan.FromDays(1));
            Assert.Equal("BTC", pp.Currency);
            Assert.True(pp.AutoApproveClaims);
            Assert.Equal(1, pp.Amount);
            Assert.Equal(pp.Name, $"Refund {invoice.Id}");

            // test RefundVariant.CurrentRate
            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.CurrentRate
            });
            Assert.Equal("BTC", pp.Currency);
            Assert.True(pp.AutoApproveClaims);
            Assert.Equal(1, pp.Amount);

            // test RefundVariant.Fiat
            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.Fiat,
                Name = "my test name"
            });
            Assert.Equal("USD", pp.Currency);
            Assert.False(pp.AutoApproveClaims);
            Assert.Equal(5000, pp.Amount);
            Assert.Equal("my test name", pp.Name);

            // test RefundVariant.Custom
            validationError = await AssertValidationError(new[] { "CustomAmount", "CustomCurrency" }, async () =>
            {
                await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
                {
                    PayoutMethodId = method.PaymentMethodId,
                    RefundVariant = RefundVariant.Custom,
                });
            });
            Assert.Contains("CustomAmount: Amount must be greater than 0", validationError.Message);
            Assert.Contains("CustomCurrency: Invalid currency", validationError.Message);

            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.Custom,
                CustomAmount = 69420,
                CustomCurrency = "JPY"
            });
            Assert.Equal("JPY", pp.Currency);
            Assert.False(pp.AutoApproveClaims);
            Assert.Equal(69420, pp.Amount);

            // should auto-approve if currencies match
            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest()
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.Custom,
                CustomAmount = 0.00069420m,
                CustomCurrency = "BTC"
            });
            Assert.True(pp.AutoApproveClaims);

            // test subtract percentage
            validationError = await AssertValidationError(new[] { "SubtractPercentage" }, async () =>
            {
                await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest
                {
                    PayoutMethodId = method.PaymentMethodId,
                    RefundVariant = RefundVariant.RateThen,
                    SubtractPercentage = 101
                });
            });
            Assert.Contains("SubtractPercentage: Percentage must be a numeric value between 0 and 100", validationError.Message);

            // should auto-approve
            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.RateThen,
                SubtractPercentage = 6.15m
            });
            Assert.Equal("BTC", pp.Currency);
            Assert.True(pp.AutoApproveClaims);
            Assert.Equal(0.9385m, pp.Amount);

            // test RefundVariant.OverpaidAmount
            validationError = await AssertValidationError(new[] { "RefundVariant" }, async () =>
            {
                await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest
                {
                    PayoutMethodId = method.PaymentMethodId,
                    RefundVariant = RefundVariant.OverpaidAmount
                });
            });
            Assert.Contains("Invoice is not overpaid", validationError.Message);

            // should auto-approve
            invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest { Amount = 5000.0m, Currency = "USD" });
            methods = await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id);
            method = methods.First();
            Assert.Equal(JTokenType.Null, method.AdditionalData["accountDerivation"].Type);
            Assert.NotNull(method.AdditionalData["keyPath"]);

            methods = await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id, includeSensitive: true);
            method = methods.First();
            Assert.Equal(JTokenType.String, method.AdditionalData["accountDerivation"].Type);
            var clientViewOnly = await user.CreateClient(Policies.CanViewInvoices);
            await AssertApiError(403, "missing-permission", () => clientViewOnly.GetInvoicePaymentMethods(user.StoreId, invoice.Id, includeSensitive: true));

            await tester.WaitForEvent<NewOnChainTransactionEvent>(async () =>
            {
                await tester.ExplorerNode.SendToAddressAsync(
                    BitcoinAddress.Create(method.Destination, tester.NetworkProvider.BTC.NBitcoinNetwork),
                    Money.Coins(method.Due * 2)
                );
            });

            await tester.ExplorerNode.GenerateAsync(5);

            await TestUtils.EventuallyAsync(async () =>
            {
                invoice = await client.GetInvoice(user.StoreId, invoice.Id);
                Assert.True(invoice.Status == InvoiceStatus.Settled);
                Assert.True(invoice.AdditionalStatus == InvoiceExceptionStatus.PaidOver);
                Assert.Equal(10000m, invoice.PaidAmount); // paid twice the amount needed...
            });

            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.OverpaidAmount
            });
            Assert.Equal("BTC", pp.Currency);
            Assert.True(pp.AutoApproveClaims);
            Assert.Equal(method.Due, pp.Amount);

            // once more with subtract percentage
            pp = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.OverpaidAmount,
                SubtractPercentage = 21m
            });
            Assert.Equal("BTC", pp.Currency);
            Assert.True(pp.AutoApproveClaims);
            Assert.Equal(0.79m, pp.Amount);

            // If an invoice doesn't have payment because it has been marked as paid, we should still be able to refund it.
            invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest { Amount = 5000.0m, Currency = "USD" });
            await client.MarkInvoiceStatus(user.StoreId, invoice.Id, new MarkInvoiceStatusRequest { Status = InvoiceStatus.Settled });
            var refund = await client.RefundInvoice(user.StoreId, invoice.Id, new RefundInvoiceRequest
            {
                PayoutMethodId = method.PaymentMethodId,
                RefundVariant = RefundVariant.CurrentRate
            });
            Assert.Equal(1.0m, refund.Amount);
            Assert.Equal("BTC", refund.Currency);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task InvoiceTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);
            await user.MakeAdmin();
            await user.SetupWebhook();
            var client = await user.CreateClient(Policies.Unrestricted);
            var viewOnly = await user.CreateClient(Policies.CanViewInvoices);

            //create

            //validation errors
            await AssertValidationError(new[] { nameof(CreateInvoiceRequest.Amount), $"{nameof(CreateInvoiceRequest.Checkout)}.{nameof(CreateInvoiceRequest.Checkout.PaymentTolerance)}", $"{nameof(CreateInvoiceRequest.Checkout)}.{nameof(CreateInvoiceRequest.Checkout.PaymentMethods)}[0]" }, async () =>
            {
                await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest { Amount = -1, Checkout = new CreateInvoiceRequest.CheckoutOptions { PaymentTolerance = -2, PaymentMethods = new[] { "jasaas_sdsad" } } });
            });

            await AssertHttpError(403, async () =>
            {
                await viewOnly.CreateInvoice(user.StoreId,
                    new CreateInvoiceRequest { Currency = "helloinvalid", Amount = 1 });
            });
            await user.RegisterDerivationSchemeAsync("BTC");
            string origOrderId = "testOrder";
            var newInvoice = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest
                {
                    Currency = "USD",
                    Amount = 1,
                    Metadata = JObject.Parse($"{{\"itemCode\": \"testitem\", \"orderId\": \"{origOrderId}\"}}"),
                    Checkout = new CreateInvoiceRequest.CheckoutOptions()
                    {
                        RedirectAutomatically = true
                    },
                    AdditionalSearchTerms = new string[] { "Banana" }
                });
            Assert.True(newInvoice.Checkout.RedirectAutomatically);
            Assert.Equal(user.StoreId, newInvoice.StoreId);
            //list
            var invoices = await viewOnly.GetInvoices(user.StoreId);

            Assert.NotNull(invoices);
            Assert.Single(invoices);
            Assert.Equal(newInvoice.Id, invoices.First().Id);

            invoices = await viewOnly.GetInvoices(user.StoreId, textSearch: "Banana");
            Assert.NotNull(invoices);
            Assert.Single(invoices);
            Assert.Equal(newInvoice.Id, invoices.First().Id);

            invoices = await viewOnly.GetInvoices(user.StoreId, textSearch: "apples");
            Assert.NotNull(invoices);
            Assert.Empty(invoices);

            //list Filtered
            var invoicesFiltered = await viewOnly.GetInvoices(user.StoreId,
                orderId: null, status: null, startDate: DateTimeOffset.Now.AddHours(-1),
                endDate: DateTimeOffset.Now.AddHours(1));

            Assert.NotNull(invoicesFiltered);
            Assert.Single(invoicesFiltered);
            Assert.Equal(newInvoice.Id, invoicesFiltered.First().Id);


            Assert.NotNull(invoicesFiltered);
            Assert.Single(invoicesFiltered);
            Assert.Equal(newInvoice.Id, invoicesFiltered.First().Id);

            //list Yesterday
            var invoicesYesterday = await viewOnly.GetInvoices(user.StoreId,
                orderId: null, status: null, startDate: DateTimeOffset.Now.AddDays(-2),
                endDate: DateTimeOffset.Now.AddDays(-1));
            Assert.NotNull(invoicesYesterday);
            Assert.Empty(invoicesYesterday);

            // Error, startDate and endDate inverted
            await AssertValidationError(new[] { "startDate", "endDate" },
                () => viewOnly.GetInvoices(user.StoreId,
                orderId: null, status: null, startDate: DateTimeOffset.Now.AddDays(-1),
                endDate: DateTimeOffset.Now.AddDays(-2)));

            await AssertValidationError(new[] { "startDate" },
                () => viewOnly.SendHttpRequest<Client.Models.InvoiceData[]>($"api/v1/stores/{user.StoreId}/invoices", new Dictionary<string, object>()
                {
                    { "startDate", "blah" }
                }));


            //list Existing OrderId
            var invoicesExistingOrderId =
                    await viewOnly.GetInvoices(user.StoreId, orderId: new[] { newInvoice.Metadata["orderId"].ToString() });
            Assert.NotNull(invoicesExistingOrderId);
            Assert.Single(invoicesFiltered);
            Assert.Equal(newInvoice.Id, invoicesFiltered.First().Id);

            //list NonExisting OrderId
            var invoicesNonExistingOrderId =
                await viewOnly.GetInvoices(user.StoreId, orderId: new[] { "NonExistingOrderId" });
            Assert.NotNull(invoicesNonExistingOrderId);
            Assert.Empty(invoicesNonExistingOrderId);

            //list Existing Status
            var invoicesExistingStatus =
                await viewOnly.GetInvoices(user.StoreId, status: new[] { newInvoice.Status });
            Assert.NotNull(invoicesExistingStatus);
            Assert.Single(invoicesExistingStatus);
            Assert.Equal(newInvoice.Id, invoicesExistingStatus.First().Id);

            //list NonExisting Status
            var invoicesNonExistingStatus = await viewOnly.GetInvoices(user.StoreId,
                status: new[] { InvoiceStatus.Invalid });
            Assert.NotNull(invoicesNonExistingStatus);
            Assert.Empty(invoicesNonExistingStatus);


            //get
            var invoice = await viewOnly.GetInvoice(user.StoreId, newInvoice.Id);
            Assert.True(JObject.DeepEquals(newInvoice.Metadata, invoice.Metadata));
            var paymentMethods = await viewOnly.GetInvoicePaymentMethods(user.StoreId, newInvoice.Id);
            Assert.Single(paymentMethods);
            var paymentMethod = paymentMethods.First();
            Assert.Equal("BTC-CHAIN", paymentMethod.PaymentMethodId);
            Assert.Equal("BTC", paymentMethod.Currency);
            Assert.Empty(paymentMethod.Payments);


            //update
            newInvoice = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest { Currency = "USD", Amount = 1 });
            Assert.Contains(InvoiceStatus.Settled, newInvoice.AvailableStatusesForManualMarking);
            Assert.Contains(InvoiceStatus.Invalid, newInvoice.AvailableStatusesForManualMarking);
            await client.MarkInvoiceStatus(user.StoreId, newInvoice.Id, new MarkInvoiceStatusRequest()
            {
                Status = InvoiceStatus.Settled
            });
            newInvoice = await client.GetInvoice(user.StoreId, newInvoice.Id);

            Assert.DoesNotContain(InvoiceStatus.Settled, newInvoice.AvailableStatusesForManualMarking);
            Assert.Contains(InvoiceStatus.Invalid, newInvoice.AvailableStatusesForManualMarking);
            newInvoice = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest { Currency = "USD", Amount = 1 });
            await client.MarkInvoiceStatus(user.StoreId, newInvoice.Id, new MarkInvoiceStatusRequest()
            {
                Status = InvoiceStatus.Invalid
            });

            newInvoice = await client.GetInvoice(user.StoreId, newInvoice.Id);

            const string newOrderId = "UPDATED-ORDER-ID";
            JObject metadataForUpdate = JObject.Parse($"{{\"orderId\": \"{newOrderId}\", \"itemCode\": \"updated\", \"newstuff\": [1,2,3,4,5]}}");
            Assert.Contains(InvoiceStatus.Settled, newInvoice.AvailableStatusesForManualMarking);
            Assert.DoesNotContain(InvoiceStatus.Invalid, newInvoice.AvailableStatusesForManualMarking);
            await AssertHttpError(403, async () =>
            {
                await viewOnly.UpdateInvoice(user.StoreId, invoice.Id,
                    new UpdateInvoiceRequest
                    {
                        Metadata = metadataForUpdate
                    });
            });
            invoice = await client.UpdateInvoice(user.StoreId, invoice.Id,
                new UpdateInvoiceRequest
                {
                    Metadata = metadataForUpdate
                });

            Assert.Equal(newOrderId, invoice.Metadata["orderId"].Value<string>());
            Assert.Equal("updated", invoice.Metadata["itemCode"].Value<string>());
            Assert.Equal(15, ((JArray)invoice.Metadata["newstuff"]).Values<int>().Sum());

            //also test the metadata actually got saved
            invoice = await client.GetInvoice(user.StoreId, invoice.Id);
            Assert.Equal(newOrderId, invoice.Metadata["orderId"].Value<string>());
            Assert.Equal("updated", invoice.Metadata["itemCode"].Value<string>());
            Assert.Equal(15, ((JArray)invoice.Metadata["newstuff"]).Values<int>().Sum());

            // test if we can find the updated invoice using the new orderId
            var invoicesWithOrderId = await client.GetInvoices(user.StoreId, new[] { newOrderId });
            Assert.NotNull(invoicesWithOrderId);
            Assert.Single(invoicesWithOrderId);
            Assert.Equal(invoice.Id, invoicesWithOrderId.First().Id);

            // test if the old orderId does not yield any results anymore
            var invoicesWithOldOrderId = await client.GetInvoices(user.StoreId, new[] { origOrderId });
            Assert.NotNull(invoicesWithOldOrderId);
            Assert.Empty(invoicesWithOldOrderId);

            //archive
            await AssertHttpError(403, async () =>
            {
                await viewOnly.ArchiveInvoice(user.StoreId, invoice.Id);
            });

            await client.ArchiveInvoice(user.StoreId, invoice.Id);
            Assert.DoesNotContain(invoice.Id,
                (await client.GetInvoices(user.StoreId)).Select(data => data.Id));

            //unarchive
            await client.UnarchiveInvoice(user.StoreId, invoice.Id);
            Assert.NotNull(await client.GetInvoice(user.StoreId, invoice.Id));

            foreach (var marked in new[] { InvoiceStatus.Settled, InvoiceStatus.Invalid })
            {
                var inv = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest { Currency = "USD", Amount = 100 });
                await user.PayInvoice(inv.Id);
                await client.MarkInvoiceStatus(user.StoreId, inv.Id, new MarkInvoiceStatusRequest
                {
                    Status = marked
                });
                var result = await client.GetInvoice(user.StoreId, inv.Id);
                if (marked == InvoiceStatus.Settled)
                {
                    Assert.Equal(InvoiceStatus.Settled, result.Status);
                    await user.AssertHasWebhookEvent<WebhookInvoiceSettledEvent>(WebhookEventType.InvoiceSettled,
                        o =>
                        {
                            Assert.Equal(inv.Id, o.InvoiceId);
                            Assert.True(o.ManuallyMarked);
                        });
                }
                if (marked == InvoiceStatus.Invalid)
                {
                    Assert.Equal(InvoiceStatus.Invalid, result.Status);
                    var evt = await user.AssertHasWebhookEvent<WebhookInvoiceInvalidEvent>(WebhookEventType.InvoiceInvalid,
                        o =>
                        {
                            Assert.Equal(inv.Id, o.InvoiceId);
                            Assert.True(o.ManuallyMarked);
                        });
                    Assert.NotNull(await client.GetWebhookDelivery(evt.StoreId, evt.WebhookId, evt.DeliveryId));
                }
            }

            newInvoice = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest
                {
                    Currency = "USD",
                    Amount = 1,
                    Checkout = new CreateInvoiceRequest.CheckoutOptions
                    {
                        DefaultLanguage = "it-it ",
                        RedirectURL = "http://toto.com/lol"
                    }
                });
            var invoiceObject = await client.GetOnChainWalletObject(user.StoreId, "BTC", new OnChainWalletObjectId("invoice", newInvoice.Id), false);
            Assert.Contains(invoiceObject.Links.Select(l => l.Type), t => t == "address");

            Assert.EndsWith($"/i/{newInvoice.Id}", newInvoice.CheckoutLink);
            var controller = tester.PayTester.GetController<UIInvoiceController>(user.UserId, user.StoreId);
            var model = (CheckoutModel)((ViewResult)await controller.Checkout(newInvoice.Id)).Model;
            Assert.Equal("it-IT", model.DefaultLang);
            Assert.Equal("http://toto.com/lol", model.MerchantRefLink);

            var langs = tester.PayTester.GetService<LanguageService>();
            foreach (var match in new[] { "it", "it-IT", "it-LOL" })
            {
                Assert.Equal("it-IT", langs.FindLanguage(match).Code);
            }
            foreach (var match in new[] { "pt-BR" })
            {
                Assert.Equal("pt-BR", langs.FindLanguage(match).Code);
            }
            foreach (var match in new[] { "en", "en-US" })
            {
                Assert.Equal("en", langs.FindLanguage(match).Code);
            }
            foreach (var match in new[] { "pt", "pt-pt", "pt-PT" })
            {
                Assert.Equal("pt-PT", langs.FindLanguage(match).Code);
            }

            //payment method activation tests
            var store = await client.GetStore(user.StoreId);
            Assert.False(store.LazyPaymentMethods);
            store.LazyPaymentMethods = true;
            store = await client.UpdateStore(store.Id,
                JObject.FromObject(store).ToObject<UpdateStoreRequest>());
            Assert.True(store.LazyPaymentMethods);

            invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest() { Amount = 1, Currency = "USD" });
            invoiceObject = await client.GetOnChainWalletObject(user.StoreId, "BTC", new OnChainWalletObjectId("invoice", invoice.Id), false);
            Assert.DoesNotContain(invoiceObject.Links.Select(l => l.Type), t => t == "address");

            // Check if we can get the monitored invoice
            var invoiceRepo = tester.PayTester.GetService<InvoiceRepository>();
            var includeNonActivated = true;
            Assert.Single(await invoiceRepo.GetMonitoredInvoices(PaymentMethodId.Parse("BTC-CHAIN"), includeNonActivated), i => i.Id == invoice.Id);
            includeNonActivated = false;
            Assert.DoesNotContain(await invoiceRepo.GetMonitoredInvoices(PaymentMethodId.Parse("BTC-CHAIN"), includeNonActivated), i => i.Id == invoice.Id);
            Assert.DoesNotContain(await invoiceRepo.GetMonitoredInvoices(PaymentMethodId.Parse("BTC-CHAIN")), i => i.Id == invoice.Id);
            //

            paymentMethods = await client.GetInvoicePaymentMethods(store.Id, invoice.Id);
            Assert.Single(paymentMethods);
            Assert.False(paymentMethods.First().Activated);
            await client.ActivateInvoicePaymentMethod(user.StoreId, invoice.Id,
                paymentMethods.First().PaymentMethodId);
            invoiceObject = await client.GetOnChainWalletObject(user.StoreId, "BTC", new OnChainWalletObjectId("invoice", invoice.Id), false);
            Assert.Contains(invoiceObject.Links.Select(l => l.Type), t => t == "address");

            paymentMethods = await client.GetInvoicePaymentMethods(store.Id, invoice.Id);
            Assert.Single(paymentMethods);
            Assert.True(paymentMethods.First().Activated);

            var invoiceWithDefaultPaymentMethodLN = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest()
                {
                    Currency = "USD",
                    Amount = 100,
                    Checkout = new CreateInvoiceRequest.CheckoutOptions()
                    {
                        PaymentMethods = new[] { "BTC", "BTC-LightningNetwork" },
                        DefaultPaymentMethod = "BTC_LightningLike"
                    }
                });
            Assert.Equal("BTC-LN", invoiceWithDefaultPaymentMethodLN.Checkout.DefaultPaymentMethod);

            var invoiceWithDefaultPaymentMethodOnChain = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest()
                {
                    Currency = "USD",
                    Amount = 100,
                    Checkout = new CreateInvoiceRequest.CheckoutOptions()
                    {
                        PaymentMethods = new[] { "BTC", "BTC-LightningNetwork" },
                        DefaultPaymentMethod = "BTC"
                    }
                });
            Assert.Equal("BTC-CHAIN", invoiceWithDefaultPaymentMethodOnChain.Checkout.DefaultPaymentMethod);

            // reset lazy payment methods
            store = await client.GetStore(user.StoreId);
            store.LazyPaymentMethods = false;
            store = await client.UpdateStore(store.Id,
                JObject.FromObject(store).ToObject<UpdateStoreRequest>());
            Assert.False(store.LazyPaymentMethods);

            // use store default payment method
            store = await client.GetStore(user.StoreId);
            Assert.Null(store.DefaultPaymentMethod);
            var storeDefaultPaymentMethod = "BTC-LN";
            store.DefaultPaymentMethod = storeDefaultPaymentMethod;
            store = await client.UpdateStore(store.Id,
                JObject.FromObject(store).ToObject<UpdateStoreRequest>());
            Assert.Equal(storeDefaultPaymentMethod, store.DefaultPaymentMethod);

            var invoiceWithStoreDefaultPaymentMethod = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest()
                {
                    Currency = "USD",
                    Amount = 100,
                    Checkout = new CreateInvoiceRequest.CheckoutOptions()
                    {
                        PaymentMethods = new[] { "BTC", "BTC-LightningNetwork", "BTC_LightningLike" }
                    }
                });
            Assert.Null(invoiceWithStoreDefaultPaymentMethod.Checkout.DefaultPaymentMethod);

            //let's see the overdue amount
            invoice = await client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest()
                {
                    Currency = "BTC",
                    Amount = 0.0001m,
                    Checkout = new CreateInvoiceRequest.CheckoutOptions()
                    {
                        PaymentMethods = new[] { "BTC" },
                        DefaultPaymentMethod = "BTC"
                    }
                });
            var pm = Assert.Single(await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id));
            Assert.Equal(0.0001m, pm.Due);

            await tester.WaitForEvent<NewOnChainTransactionEvent>(async () =>
            {
                await tester.ExplorerNode.SendToAddressAsync(
                    BitcoinAddress.Create(pm.Destination, tester.ExplorerClient.Network.NBitcoinNetwork),
                    new Money(0.0002m, MoneyUnit.BTC));
            });

            await TestUtils.EventuallyAsync(async () =>
            {
                var pm = Assert.Single(await client.GetInvoicePaymentMethods(user.StoreId, invoice.Id));
                Assert.Single(pm.Payments);
                Assert.Equal(-0.0001m, pm.Due);

                invoiceObject = await client.GetOnChainWalletObject(user.StoreId, "BTC", new OnChainWalletObjectId("invoice", invoice.Id), false);
                Assert.Contains(invoiceObject.Links.Select(l => l.Type), t => t == "tx");
            });

            // retrieve invoice refund trigger data
            var accounting = await client.GetInvoiceRefundTriggerData(store.Id, invoice.Id, paymentMethod.PaymentMethodId);
            Assert.NotNull(accounting);
            Assert.Equal("BTC", accounting.InvoiceCurrency);
            Assert.Equal(0.0002M, accounting.PaymentAmountThen);
            Assert.Equal(0.0002M, accounting.PaymentAmountNow);
            Assert.Equal(0.0001M, accounting.OverpaidPaymentAmount);
            Assert.True(accounting.InvoiceAmount > 0);
        }

        [Fact(Timeout = 60 * 20 * 1000)]
        [Trait("Integration", "Integration")]
        [Trait("Lightning", "Lightning")]
        public async Task CanUseLightningAPI()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);
            user.RegisterLightningNode("BTC", LightningConnectionType.CLightning, false);

            var merchant = tester.NewAccount();
            await merchant.GrantAccessAsync(true);
            merchant.RegisterLightningNode("BTC", LightningConnectionType.LndREST);
            var merchantClient = await merchant.CreateClient($"{Policies.CanUseLightningNodeInStore}:{merchant.StoreId}");
            var merchantInvoice = await merchantClient.CreateLightningInvoice(merchant.StoreId, "BTC", new CreateLightningInvoiceRequest(LightMoney.Satoshis(1_000), "hey", TimeSpan.FromSeconds(60)));
            Assert.NotNull(merchantInvoice.Id);
            Assert.NotNull(merchantInvoice.PaymentHash);
            Assert.Equal(merchantInvoice.Id, merchantInvoice.PaymentHash);

            var client = await user.CreateClient(Policies.CanUseInternalLightningNode);
            // Not permission for the store!
            await AssertAPIError("missing-permission", () => client.GetLightningNodeChannels(user.StoreId, "BTC"));
            var invoiceData = await client.CreateLightningInvoice("BTC", new CreateLightningInvoiceRequest()
            {
                Amount = LightMoney.Satoshis(1000),
                Description = "lol",
                Expiry = TimeSpan.FromSeconds(400),
                PrivateRouteHints = false
            });
            Assert.NotNull(await client.GetLightningInvoice("BTC", invoiceData.Id));

            // check list for internal node
            var invoices = await client.GetLightningInvoices("BTC");
            var pendingInvoices = await client.GetLightningInvoices("BTC", true);
            Assert.NotEmpty(invoices);
            Assert.Contains(invoices, i => i.Id == invoiceData.Id);
            Assert.NotEmpty(pendingInvoices);
            Assert.Contains(pendingInvoices, i => i.Id == invoiceData.Id);

            client = await user.CreateClient($"{Policies.CanUseLightningNodeInStore}:{user.StoreId}");
            // Not permission for the server
            await AssertAPIError("missing-permission", () => client.GetLightningNodeChannels("BTC"));

            var data = await client.GetLightningNodeChannels(user.StoreId, "BTC");
            Assert.Equal(2, data.Count());
            BitcoinAddress.Create(await client.GetLightningDepositAddress(user.StoreId, "BTC"), Network.RegTest);

            invoiceData = await client.CreateLightningInvoice(user.StoreId, "BTC", new CreateLightningInvoiceRequest()
            {
                Amount = LightMoney.Satoshis(1000),
                Description = "lol",
                Expiry = TimeSpan.FromSeconds(400),
                PrivateRouteHints = false
            });

            Assert.NotNull(await client.GetLightningInvoice(user.StoreId, "BTC", invoiceData.Id));

            // check pending list
            var merchantPendingInvoices = await merchantClient.GetLightningInvoices(merchant.StoreId, "BTC", true);
            Assert.NotEmpty(merchantPendingInvoices);
            Assert.Contains(merchantPendingInvoices, i => i.Id == merchantInvoice.Id);

            var payResponse = await client.PayLightningInvoice(user.StoreId, "BTC", new PayLightningInvoiceRequest
            {
                BOLT11 = merchantInvoice.BOLT11
            });
            Assert.Equal(merchantInvoice.BOLT11, payResponse.BOLT11);
            Assert.Equal(LightningPaymentStatus.Complete, payResponse.Status);
            Assert.NotNull(payResponse.Preimage);
            Assert.NotNull(payResponse.FeeAmount);
            Assert.NotNull(payResponse.TotalAmount);
            Assert.NotNull(payResponse.PaymentHash);

            // check the get invoice response
            var merchInvoice = await merchantClient.GetLightningInvoice(merchant.StoreId, "BTC", merchantInvoice.Id);
            Assert.NotNull(merchInvoice);
            Assert.NotNull(merchInvoice.Preimage);
            Assert.NotNull(merchInvoice.PaymentHash);
            Assert.Equal(payResponse.Preimage, merchInvoice.Preimage);
            Assert.Equal(payResponse.PaymentHash, merchInvoice.PaymentHash);

            await Assert.ThrowsAsync<GreenfieldValidationException>(async () => await client.PayLightningInvoice(user.StoreId, "BTC", new PayLightningInvoiceRequest()
            {
                BOLT11 = "lol"
            }));

            var validationErr = await Assert.ThrowsAsync<GreenfieldValidationException>(async () => await client.CreateLightningInvoice(user.StoreId, "BTC", new CreateLightningInvoiceRequest()
            {
                Amount = -1,
                Expiry = TimeSpan.FromSeconds(-1),
                Description = null
            }));
            Assert.Equal(2, validationErr.ValidationErrors.Length);

            var invoice = await merchantClient.GetLightningInvoice(merchant.StoreId, "BTC", merchantInvoice.Id);
            Assert.NotNull(invoice.PaidAt);
            Assert.NotNull(invoice.PaymentHash);
            Assert.NotNull(invoice.Preimage);
            Assert.Equal(LightMoney.Satoshis(1000), invoice.Amount);

            // check list for store with paid invoice
            var merchantInvoices = await merchantClient.GetLightningInvoices(merchant.StoreId, "BTC");
            Assert.NotEmpty(merchantInvoices);
            merchantPendingInvoices = await merchantClient.GetLightningInvoices(merchant.StoreId, "BTC", true);
            Assert.True(merchantPendingInvoices.Length < merchantInvoices.Length);
            Assert.All(merchantPendingInvoices, m => Assert.Equal(LightningInvoiceStatus.Unpaid, m.Status));
            // if the test ran too many times the invoice might be on a later page
            if (merchantInvoices.Length < 100)
                Assert.Contains(merchantInvoices, i => i.Id == merchantInvoice.Id);

            // Amount received might be bigger because of internal implementation shit from lightning
            Assert.True(LightMoney.Satoshis(1000) <= invoice.AmountReceived);

            // check payments list for store node
            var payments = await client.GetLightningPayments(user.StoreId, "BTC");
            Assert.NotEmpty(payments);
            Assert.Contains(payments, i => i.BOLT11 == merchantInvoice.BOLT11);

            // Node info
            var info = await client.GetLightningNodeInfo(user.StoreId, "BTC");
            Assert.Single(info.NodeURIs);
            Assert.NotEqual(0, info.BlockHeight);

            // Disable for now see #6518
            //// balance
            //await TestUtils.EventuallyAsync(async () =>
            //{
            //    var balance = await client.GetLightningNodeBalance(user.StoreId, "BTC");
            //    var localBalance = balance.OffchainBalance.Local.ToDecimal(LightMoneyUnit.BTC);
            //    var histogram = await client.GetLightningNodeHistogram(user.StoreId, "BTC");
            //    Assert.Equal(histogram.Balance, histogram.Series.Last());
            //    Assert.Equal(localBalance, histogram.Balance);
            //    Assert.Equal(localBalance, histogram.Series.Last());
            //});

            // As admin, can use the internal node through our store.
            await user.MakeAdmin(true);
            await user.RegisterInternalLightningNodeAsync("BTC");
            await client.GetLightningNodeInfo(user.StoreId, "BTC");
            // But if not admin anymore, nope
            await user.MakeAdmin(false);
            await AssertPermissionError("btcpay.server.canuseinternallightningnode", () => client.GetLightningNodeInfo(user.StoreId, "BTC"));
            // However, even as a guest, you should be able to create an invoice
            var guest = tester.NewAccount();
            await guest.GrantAccessAsync();
            await user.AddGuest(guest.UserId);
            client = await guest.CreateClient(Policies.CanCreateLightningInvoiceInStore);
            await client.CreateLightningInvoice(user.StoreId, "BTC", new CreateLightningInvoiceRequest()
            {
                Amount = LightMoney.Satoshis(1000),
                Description = "lol",
                Expiry = TimeSpan.FromSeconds(600),
            });
            client = await guest.CreateClient(Policies.CanUseLightningNodeInStore);
            // Can use lightning node is only granted to store's owner
            await AssertPermissionError("btcpay.store.canuselightningnode", () => client.GetLightningNodeInfo(user.StoreId, "BTC"));

            // balance and histogram should not be accessible with view only clients
            await AssertPermissionError("btcpay.store.canuselightningnode", () => client.GetLightningNodeBalance(user.StoreId, "BTC"));
            await AssertPermissionError("btcpay.store.canuselightningnode", () => client.GetLightningNodeHistogram(user.StoreId, "BTC"));
        }

        [Fact(Timeout = 60 * 20 * 1000)]
        [Trait("Integration", "Integration")]
        [Trait("Lightning", "Lightning")]
        public async Task CanAccessInvoiceLightningPaymentMethodDetails()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);
            user.RegisterLightningNode("BTC", LightningConnectionType.CLightning);

            var client = await user.CreateClient(Policies.Unrestricted);
            var invoices = new Task<Client.Models.InvoiceData>[5];

            // Create invoices
            for (int i = 0; i < invoices.Length; i++)
            {
                invoices[i] = client.CreateInvoice(user.StoreId,
                new CreateInvoiceRequest
                {
                    Currency = "USD",
                    Amount = 0.1m,
                    Checkout = new CreateInvoiceRequest.CheckoutOptions
                    {
                        PaymentMethods = new[] { "BTC-LN" },
                        DefaultPaymentMethod = "BTC-LN"
                    }
                });
            }

            var pm = new InvoicePaymentMethodDataModel[invoices.Length];
            for (int i = 0; i < invoices.Length; i++)
            {
                pm[i] = Assert.Single(await client.GetInvoicePaymentMethods(user.StoreId, (await invoices[i]).Id));
                Assert.True(pm[i].AdditionalData.HasValues);
            }

            // Pay them all at once
            Task<PayResponse>[] payResponses = new Task<PayResponse>[invoices.Length];
            for (int i = 0; i < invoices.Length; i++)
            {
                payResponses[i] = tester.CustomerLightningD.Pay(pm[i].Destination);
            }

            // Checking the results
            for (int i = 0; i < invoices.Length; i++)
            {
                var resp = await payResponses[i];
                Assert.Equal(PayResult.Ok, resp.Result);
                Assert.NotNull(resp.Details.PaymentHash);
                Assert.NotNull(resp.Details.Preimage);
                await TestUtils.EventuallyAsync(async () =>
                {
                    pm[i] = Assert.Single(await client.GetInvoicePaymentMethods(user.StoreId, (await invoices[i]).Id));
                    Assert.True(pm[i].AdditionalData.HasValues);
                    Assert.Equal(resp.Details.PaymentHash.ToString(), ((JObject)pm[i].AdditionalData).GetValue("paymentHash"));
                    Assert.Equal(resp.Details.Preimage.ToString(), ((JObject)pm[i].AdditionalData).GetValue("preimage"));
                });
            }
        }

        [Fact(Timeout = 60 * 20 * 1000)]
        [Trait("Integration", "Integration")]
        [Trait("Lightning", "Lightning")]
        public async Task CanUseLightningAPI2()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);

            var types = new[] { LightningConnectionType.LndREST, LightningConnectionType.CLightning };
            foreach (var type in types)
            {
                user.RegisterLightningNode("BTC", type);
                var client = await user.CreateClient("btcpay.store.cancreatelightninginvoice");
                var amount = LightMoney.Satoshis(1000);
                var expiry = TimeSpan.FromSeconds(600);

                var invoice = await client.CreateLightningInvoice(user.StoreId, "BTC", new CreateLightningInvoiceRequest
                {
                    Amount = amount,
                    Expiry = expiry,
                    Description = "Hashed description",
                    DescriptionHashOnly = true
                });
                var bolt11 = BOLT11PaymentRequest.Parse(invoice.BOLT11, Network.RegTest);
                Assert.NotNull(bolt11.DescriptionHash);
                Assert.Null(bolt11.ShortDescription);

                invoice = await client.CreateLightningInvoice(user.StoreId, "BTC", new CreateLightningInvoiceRequest
                {
                    Amount = amount,
                    Expiry = expiry,
                    Description = "Standard description",
                });
                bolt11 = BOLT11PaymentRequest.Parse(invoice.BOLT11, Network.RegTest);
                Assert.Null(bolt11.DescriptionHash);
                Assert.NotNull(bolt11.ShortDescription);
            }
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task NotificationAPITests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);
            var client = await user.CreateClient(Policies.CanManageNotificationsForUser);
            var viewOnlyClient = await user.CreateClient(Policies.CanViewNotificationsForUser);
            await tester.PayTester.GetService<NotificationSender>()
                .SendNotification(new UserScope(user.UserId), new NewVersionNotification());

            var notifications = (await viewOnlyClient.GetNotifications()).ToList();
            Assert.Single(notifications);
            Assert.Single(await viewOnlyClient.GetNotifications(false));
            Assert.Empty(await viewOnlyClient.GetNotifications(true));

            var notification = notifications.First();
            Assert.Null(notification.StoreId);

            Assert.Single(await client.GetNotifications());
            Assert.Single(await client.GetNotifications(false));
            Assert.Empty(await client.GetNotifications(true));
            notification = (await client.GetNotifications()).First();
            notification = await client.GetNotification(notification.Id);
            Assert.False(notification.Seen);
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.UpdateNotification(notification.Id, true);
            });
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.RemoveNotification(notification.Id);
            });
            Assert.True((await client.UpdateNotification(notification.Id, true)).Seen);
            Assert.Single(await viewOnlyClient.GetNotifications(true));
            Assert.Empty(await viewOnlyClient.GetNotifications(false));
            await client.RemoveNotification(notification.Id);
            Assert.Empty(await viewOnlyClient.GetNotifications(true));
            Assert.Empty(await viewOnlyClient.GetNotifications(false));

            // Store association
            var unrestricted = await user.CreateClient(Policies.Unrestricted);
            var store1 = await unrestricted.CreateStore(new CreateStoreRequest { Name = "Store A" });
            await tester.PayTester.GetService<NotificationSender>()
                .SendNotification(new UserScope(user.UserId), new InviteAcceptedNotification{
                    UserId = user.UserId,
                    UserEmail = user.Email,
                    StoreId = store1.Id,
                    StoreName = store1.Name
                });
            notifications = (await client.GetNotifications()).ToList();
            Assert.Single(notifications);

            notification = notifications.First();
            Assert.Equal(store1.Id, notification.StoreId);
            Assert.Equal($"User {user.Email} accepted the invite to {store1.Name}.", notification.Body);

            var store2 = await unrestricted.CreateStore(new CreateStoreRequest { Name = "Store B" });
            await tester.PayTester.GetService<NotificationSender>()
                .SendNotification(new UserScope(user.UserId), new InviteAcceptedNotification{
                    UserId = user.UserId,
                    UserEmail = user.Email,
                    StoreId = store2.Id,
                    StoreName = store2.Name
                });
            notifications = (await client.GetNotifications(storeId: [store2.Id])).ToList();
            Assert.Single(notifications);

            notification = notifications.First();
            Assert.Equal(store2.Id, notification.StoreId);
            Assert.Equal($"User {user.Email} accepted the invite to {store2.Name}.", notification.Body);

            Assert.Equal(2, (await client.GetNotifications(storeId: [store1.Id, store2.Id])).Count());
            Assert.Equal(2, (await client.GetNotifications()).Count());

            // Settings
            var settings = await client.GetNotificationSettings();
            Assert.True(settings.Notifications.Find(n => n.Identifier == "newversion").Enabled);
            Assert.True(settings.Notifications.Find(n => n.Identifier == "pluginupdate").Enabled);
            Assert.True(settings.Notifications.Find(n => n.Identifier == "inviteaccepted").Enabled);

            var request = new UpdateNotificationSettingsRequest { Disabled = ["newversion", "pluginupdate"] };
            settings = await client.UpdateNotificationSettings(request);
            Assert.False(settings.Notifications.Find(n => n.Identifier == "newversion").Enabled);
            Assert.False(settings.Notifications.Find(n => n.Identifier == "pluginupdate").Enabled);
            Assert.True(settings.Notifications.Find(n => n.Identifier == "inviteaccepted").Enabled);

            request = new UpdateNotificationSettingsRequest { Disabled = ["all"] };
            settings = await client.UpdateNotificationSettings(request);
            Assert.False(settings.Notifications.Find(n => n.Identifier == "newversion").Enabled);
            Assert.False(settings.Notifications.Find(n => n.Identifier == "pluginupdate").Enabled);
            Assert.False(settings.Notifications.Find(n => n.Identifier == "inviteaccepted").Enabled);

            request = new UpdateNotificationSettingsRequest { Disabled = [] };
            settings = await client.UpdateNotificationSettings(request);
            Assert.True(settings.Notifications.Find(n => n.Identifier == "newversion").Enabled);
            Assert.True(settings.Notifications.Find(n => n.Identifier == "pluginupdate").Enabled);
            Assert.True(settings.Notifications.Find(n => n.Identifier == "inviteaccepted").Enabled);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task OnChainPaymentMethodAPITests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var user = tester.NewAccount();
            var user2 = tester.NewAccount();
            await user.GrantAccessAsync(true);
            await user2.GrantAccessAsync(false);

            var client = await user.CreateClient(Policies.CanModifyStoreSettings);
            var client2 = await user2.CreateClient(Policies.CanModifyStoreSettings);
            var viewOnlyClient = await user.CreateClient(Policies.CanViewStoreSettings);

            var store = await client.CreateStore(new CreateStoreRequest() { Name = "test store" });

            Assert.Empty(await client.GetStorePaymentMethods(store.Id));
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.UpdateStorePaymentMethod(store.Id, "BTC-CHAIN", new UpdatePaymentMethodRequest() { });
            });

            var xpriv = new Mnemonic("all all all all all all all all all all all all").DeriveExtKey()
                .Derive(KeyPath.Parse("m/84'/1'/0'"));
            var xpub = xpriv.Neuter().ToString(Network.RegTest);
            var firstAddress = xpriv.Derive(KeyPath.Parse("0/0")).Neuter().GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
            await AssertHttpError(404, async () =>
            {
                await client.PreviewStoreOnChainPaymentMethodAddresses(store.Id, "BTC");
            });

            Assert.Equal(firstAddress, (await viewOnlyClient.PreviewProposedStoreOnChainPaymentMethodAddresses(store.Id, "BTC", xpub)).Addresses.First().Address);
            // Testing if the rewrite rule to old API path is working
            await viewOnlyClient.SendHttpRequest($"api/v1/stores/{store.Id}/payment-methods/onchain/BTC/preview", new JObject() { ["config"] = xpub }, HttpMethod.Post);

            var method = await client.UpdateStorePaymentMethod(store.Id, "BTC-CHAIN", new UpdatePaymentMethodRequest() { Enabled = true, Config = JValue.CreateString(xpub)});
            var method2 = await client.UpdateStorePaymentMethod(store.Id, "BTC-CHAIN", new UpdatePaymentMethodRequest() { Enabled = true, Config = new JObject() { ["derivationScheme"] = xpub, ["label"] = "test", ["accountKeyPath"] = "aaaaaaaa/84'/0'/0'" } });
            Assert.Equal("aaaaaaaa", method2.Config["accountKeySettings"][0]["rootFingerprint"].ToString());
            Assert.Equal("84'/0'/0'", method2.Config["accountKeySettings"][0]["accountKeyPath"].ToString());
            var method3 = await client.UpdateStorePaymentMethod(store.Id, "BTC-CHAIN", new UpdatePaymentMethodRequest() { Enabled = true, Config = new JObject() { ["derivationScheme"] = xpub } });

            Assert.Equal(method.ToJson(), method3.ToJson());

            Assert.Equal(firstAddress, (await viewOnlyClient.PreviewStoreOnChainPaymentMethodAddresses(store.Id, "BTC")).Addresses.First().Address);
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.RemoveStorePaymentMethod(store.Id, "BTC-CHAIN");
            });
            await client.RemoveStorePaymentMethod(store.Id, "BTC-CHAIN");
            await AssertHttpError(404, async () =>
            {
                await client.GetStorePaymentMethod(store.Id, "BTC-CHAIN");
            });

            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.GenerateOnChainWallet(store.Id, "BTC", new GenerateOnChainWalletRequest() { });
            });

            await AssertValidationError(new[] { "SavePrivateKeys", "ImportKeysToRPC" }, async () =>
              {
                  await client2.GenerateOnChainWallet(user2.StoreId, "BTC", new GenerateOnChainWalletRequest()
                  {
                      SavePrivateKeys = true,
                      ImportKeysToRPC = true
                  });
              });


            var allMnemonic = new Mnemonic("all all all all all all all all all all all all");


            await client.RemoveStorePaymentMethod(store.Id, "BTC-CHAIN");
            var generateResponse = await client.GenerateOnChainWallet(store.Id, "BTC",
                new GenerateOnChainWalletRequest() { ExistingMnemonic = allMnemonic, });

            Assert.Equal(generateResponse.Mnemonic.ToString(), allMnemonic.ToString());
            Assert.Equal(generateResponse.Config.AccountDerivation, xpub);

            await AssertAPIError("already-configured", async () =>
            {
                await client.GenerateOnChainWallet(store.Id, "BTC",
                    new GenerateOnChainWalletRequest() { ExistingMnemonic = allMnemonic, });
            });

            await client.RemoveStorePaymentMethod(store.Id, "BTC-CHAIN");
            generateResponse = await client.GenerateOnChainWallet(store.Id, "BTC",
                new GenerateOnChainWalletRequest() { });
            Assert.NotEqual(generateResponse.Mnemonic.ToString(), allMnemonic.ToString());
            Assert.Equal(generateResponse.Mnemonic.DeriveExtKey().Derive(KeyPath.Parse("m/84'/1'/0'")).Neuter().ToString(Network.RegTest), generateResponse.Config.AccountDerivation);

            await client.RemoveStorePaymentMethod(store.Id, "BTC-CHAIN");
            generateResponse = await client.GenerateOnChainWallet(store.Id, "BTC",
                new GenerateOnChainWalletRequest() { ExistingMnemonic = allMnemonic, AccountNumber = 1, Label = "test" });
            Assert.Equal("test", generateResponse.Config.Label);
            Assert.Equal(generateResponse.Mnemonic.ToString(), allMnemonic.ToString());

            Assert.Equal(new Mnemonic("all all all all all all all all all all all all").DeriveExtKey()
                .Derive(KeyPath.Parse("m/84'/1'/1'")).Neuter().ToString(Network.RegTest), generateResponse.Config.AccountDerivation);

            await client.RemoveStorePaymentMethod(store.Id, "BTC-CHAIN");
            generateResponse = await client.GenerateOnChainWallet(store.Id, "BTC",
                new GenerateOnChainWalletRequest() { WordList = Wordlist.Japanese, WordCount = WordCount.TwentyFour });

            Assert.Equal(24, generateResponse.Mnemonic.Words.Length);
            Assert.Equal(Wordlist.Japanese, generateResponse.Mnemonic.WordList);
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Lightning", "Lightning")]
        [Trait("Integration", "Integration")]
        public async Task LightningNetworkPaymentMethodAPITests()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var admin2 = tester.NewAccount();
            await admin2.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.CanModifyStoreSettings);
            var admin2Client = await admin2.CreateClient(Policies.CanModifyStoreSettings, Policies.CanModifyServerSettings);
            var viewOnlyClient = await admin.CreateClient(Policies.CanViewStoreSettings);
            var store = await adminClient.GetStore(admin.StoreId);

            Assert.Empty(await adminClient.GetStorePaymentMethods(store.Id));
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.UpdateStorePaymentMethod(store.Id, "BTC-LN", new UpdatePaymentMethodRequest() { });
            });
            await AssertHttpError(404, async () =>
            {
                await adminClient.GetStorePaymentMethod(store.Id, "BTC-LN");
            });
            await admin.RegisterLightningNodeAsync("BTC", false);

            var method = await adminClient.GetStorePaymentMethod(store.Id, "BTC-LN");
            Assert.Null(method.Config);
            method = await adminClient.GetStorePaymentMethod(store.Id, "BTC-LN", includeConfig: true);
            Assert.NotNull(method.Config);
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.RemoveStorePaymentMethod(store.Id, "BTC-LN");
            });
            await adminClient.RemoveStorePaymentMethod(store.Id, "BTC-LN");
            await AssertHttpError(404, async () =>
            {
                await adminClient.GetStorePaymentMethod(store.Id, "BTC-LN");
            });


            // Let's verify that the admin client can't change LN to unsafe connection strings without modify server settings rights
            foreach (var forbidden in new string[]
            {
                "type=clightning;server=tcp://127.0.0.1",
                "type=clightning;server=tcp://test",
                "type=clightning;server=tcp://test.lan",
                "type=clightning;server=tcp://test.local",
                "type=clightning;server=tcp://192.168.1.2",
                "type=clightning;server=unix://8.8.8.8",
                "type=clightning;server=unix://[::1]",
                "type=clightning;server=unix://[0:0:0:0:0:0:0:1]",
            })
            {
                var ex = await AssertValidationError(new[] { "ConnectionString" }, async () =>
                {
                    await adminClient.UpdateStorePaymentMethod(store.Id, "BTC-LN", new UpdatePaymentMethodRequest()
                    {
                        Config = new JObject()
                        {
                            ["connectionString"] = forbidden
                        },
                        Enabled = true
                    });
                });
                Assert.Contains("btcpay.server.canmodifyserversettings", ex.Message);
                // However, the other client should work because he has `btcpay.server.canmodifyserversettings`
                await admin2Client.UpdateStorePaymentMethod(admin2.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
                {
                    Config = new JObject()
                    {
                        ["connectionString"] = forbidden
                    },
                    Enabled = true
                });
            }
            // Allowed ip should be ok
            await adminClient.UpdateStorePaymentMethod(store.Id, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Config = new JObject()
                {
                    ["connectionString"] = "type=clightning;server=tcp://8.8.8.8"
                },
                Enabled = true
            });
            // If we strip the admin's right, he should not be able to set unsafe anymore, even if the API key is still valid
            await admin2.MakeAdmin(false);
            await AssertValidationError(new[] { "ConnectionString" }, async () =>
            {
                await admin2Client.UpdateStorePaymentMethod(admin2.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
                {
                    Config = new JObject()
                    {
                        ["connectionString"] = "type=clightning;server=tcp://127.0.0.1"
                    },
                    Enabled = true
                });
            });

            var settings = (await tester.PayTester.GetService<SettingsRepository>().GetSettingAsync<PoliciesSettings>()) ?? new PoliciesSettings();
            settings.AllowLightningInternalNodeForAll = false;
            await tester.PayTester.GetService<SettingsRepository>().UpdateSetting(settings);
            var nonAdminUser = tester.NewAccount();
            await nonAdminUser.GrantAccessAsync(false);
            var nonAdminUserClient = await nonAdminUser.CreateClient(Policies.CanModifyStoreSettings);

            await AssertHttpError(404, async () =>
            {
                await nonAdminUserClient.GetStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN");
            });
            await AssertPermissionError("btcpay.server.canuseinternallightningnode", () => nonAdminUserClient.UpdateStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = method.Enabled,
                Config = new JObject()
                {
                    ["internalNodeRef"] = "Internal Node"
                }
            }));

            settings = await tester.PayTester.GetService<SettingsRepository>().GetSettingAsync<PoliciesSettings>();
            settings.AllowLightningInternalNodeForAll = true;
            await tester.PayTester.GetService<SettingsRepository>().UpdateSetting(settings);

            await nonAdminUserClient.UpdateStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = method.Enabled,
                Config = new JObject()
                {
                    ["internalNodeRef"] = "Internal Node"
                }
            });

            // NonAdmin can't set to internal node in AllowLightningInternalNodeForAll is false, but can do other connection string
            settings = (await tester.PayTester.GetService<SettingsRepository>().GetSettingAsync<PoliciesSettings>()) ?? new PoliciesSettings();
            settings.AllowLightningInternalNodeForAll = false;
            await tester.PayTester.GetService<SettingsRepository>().UpdateSetting(settings);
            await nonAdminUserClient.UpdateStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = true,
                Config = new JObject()
                {
                    ["connectionString"] = "type=clightning;server=tcp://8.8.8.8"
                }
            });
            await AssertPermissionError("btcpay.server.canuseinternallightningnode", () => nonAdminUserClient.UpdateStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = true,
                Config = new JObject()
                {
                    ["connectionString"] = "Internal Node"
                }
            }));
            // NonAdmin add admin as owner of the store
            await nonAdminUser.AddOwner(admin.UserId);
            // Admin turn on Internal node
            adminClient = await admin.CreateClient(Policies.CanModifyStoreSettings, Policies.CanUseInternalLightningNode);
            var data = await adminClient.UpdateStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = method.Enabled,
                Config = new JObject()
                {
                    ["connectionString"] = "Internal Node"
                }
            });
            Assert.NotNull(data);
            Assert.NotNull(data.Config["internalNodeRef"]?.Value<string>());
            // Make sure that the nonAdmin can toggle enabled, ConnectionString unchanged.
            await nonAdminUserClient.UpdateStorePaymentMethod(nonAdminUser.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = !data.Enabled,
                Config = new JObject()
                {
                    ["connectionString"] = "Internal Node"
                }
            });
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task WalletAPITests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();

            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);

            var client = await user.CreateClient(Policies.CanModifyStoreSettings, Policies.CanModifyServerSettings);
            var viewOnlyClient = await user.CreateClient(Policies.CanViewStoreSettings);
            var walletId = await user.RegisterDerivationSchemeAsync("BTC", ScriptPubKeyType.Segwit, true);

            //view only clients can't do jack shit with this API
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.ShowOnChainWalletOverview(walletId.StoreId, walletId.CryptoCode);
            });
            var overview = await client.ShowOnChainWalletOverview(walletId.StoreId, walletId.CryptoCode);
            Assert.Equal(0m, overview.Balance);

            var fee = await client.GetOnChainFeeRate(walletId.StoreId, walletId.CryptoCode);
            Assert.NotNull(fee.FeeRate);

            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.GetOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode);
            });

            // Testing if the rewrite rule to old API path is working
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.SendHttpRequest($"api/v1/stores/{walletId.StoreId}/payment-methods/onchain/{walletId.CryptoCode}/wallet/address", null as object);
            });
            var address = await client.GetOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode);
            var address2 = await client.GetOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode);
            var address3 = await client.GetOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode, true);
            Assert.Equal(address.Address, address2.Address);
            Assert.NotEqual(address.Address, address3.Address);
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.GetOnChainWalletUTXOs(walletId.StoreId, walletId.CryptoCode);
            });
            Assert.Empty(await client.GetOnChainWalletUTXOs(walletId.StoreId, walletId.CryptoCode));
            uint256 txhash = null;
            await tester.WaitForEvent<NewOnChainTransactionEvent>(async () =>
            {
                txhash = await tester.ExplorerNode.SendToAddressAsync(
                    BitcoinAddress.Create(address3.Address, tester.ExplorerClient.Network.NBitcoinNetwork),
                    new Money(0.01m, MoneyUnit.BTC));
            });
            await tester.ExplorerNode.GenerateAsync(1);

            var address4 = await client.GetOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode, false);
            Assert.NotEqual(address3.Address, address4.Address);
            await client.UnReserveOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode);
            var address5 = await client.GetOnChainWalletReceiveAddress(walletId.StoreId, walletId.CryptoCode, true);
            Assert.Equal(address5.Address, address4.Address);


            var utxo = Assert.Single(await client.GetOnChainWalletUTXOs(walletId.StoreId, walletId.CryptoCode));
            Assert.Equal(0.01m, utxo.Amount);
            Assert.Equal(txhash, utxo.Outpoint.Hash);
            overview = await client.ShowOnChainWalletOverview(walletId.StoreId, walletId.CryptoCode);
            Assert.Equal(0.01m, overview.Balance);

            // histogram should not be accessible with view only clients
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.GetOnChainWalletHistogram(walletId.StoreId, walletId.CryptoCode);
            });
            var histogram = await client.GetOnChainWalletHistogram(walletId.StoreId, walletId.CryptoCode);
            Assert.Equal(histogram.Balance, histogram.Series.Last());
            Assert.Equal(0.01m, histogram.Balance);
            Assert.Equal(0.01m, histogram.Series.Last());
            Assert.Equal(0, histogram.Series.First());

            //the simplest request:
            var nodeAddress = await tester.ExplorerNode.GetNewAddressAsync();
            var createTxRequest = new CreateOnChainTransactionRequest()
            {
                Destinations =
                    new List<CreateOnChainTransactionRequest.CreateOnChainTransactionRequestDestination>()
                    {
                        new CreateOnChainTransactionRequest.CreateOnChainTransactionRequestDestination()
                        {
                            Destination = nodeAddress.ToString(), Amount = 0.001m
                        }
                    },
                FeeRate = new FeeRate(5m) //only because regtest may fail but not required
            };
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.CreateOnChainTransaction(walletId.StoreId, walletId.CryptoCode, createTxRequest);
            });
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            {
                await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                    createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
            });
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            {
                createTxRequest.ProceedWithBroadcast = false;
                await client.CreateOnChainTransaction(walletId.StoreId, walletId.CryptoCode,
                    createTxRequest);
            });
            Transaction tx;

            tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);


            Assert.NotNull(tx);
            Assert.Contains(tx.Outputs, txout => txout.IsTo(nodeAddress) && txout.Value.ToDecimal(MoneyUnit.BTC) == 0.001m);
            Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(tx)).IsAllowed);

            // no change test
            createTxRequest.NoChange = true;
            tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
            Assert.NotNull(tx);
            Assert.True(Assert.Single(tx.Outputs).IsTo(nodeAddress));
            Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(tx)).IsAllowed);

            createTxRequest.NoChange = false;

            // Validation for excluding unconfirmed UTXOs and manually selecting inputs at the same time
            await AssertValidationError(new[] { "ExcludeUnconfirmed" }, async () =>
              {
                  createTxRequest.SelectedInputs = new List<OutPoint>();
                  createTxRequest.ExcludeUnconfirmed = true;
                  tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                      createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
              });
            createTxRequest.SelectedInputs = null;
            createTxRequest.ExcludeUnconfirmed = false;

            //coin selection
            await AssertValidationError(new[] { nameof(createTxRequest.SelectedInputs) }, async () =>
              {
                  createTxRequest.SelectedInputs = new List<OutPoint>();
                  tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                      createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
              });
            createTxRequest.SelectedInputs = new List<OutPoint>()
            {
                utxo.Outpoint
            };
            tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
            createTxRequest.SelectedInputs = null;

            //destination testing
            await AssertValidationError(new[] { "Destinations" }, async () =>
             {
                 createTxRequest.Destinations[0].Amount = utxo.Amount;
                 tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                     createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
             });

            createTxRequest.Destinations[0].SubtractFromAmount = true;
            tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);


            await AssertValidationError(new[] { "Destinations[0]" }, async () =>
             {
                 createTxRequest.Destinations[0].Amount = 0m;
                 tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                     createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
             });

            //dest can be a bip21

            //cant use bip with subtractfromamount
            createTxRequest.Destinations[0].Amount = null;
            createTxRequest.Destinations[0].Destination = $"bitcoin:{nodeAddress}?amount=0.001";
            await AssertValidationError(new[] { "Destinations[0]" }, async () =>
             {
                 tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                     createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
             });
            //if amt specified, it  overrides bip21 amount
            createTxRequest.Destinations[0].Amount = 0.0001m;
            createTxRequest.Destinations[0].SubtractFromAmount = false;
            tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
            Assert.Contains(tx.Outputs, txout => txout.Value.GetValue(tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC")) == 0.0001m);

            //fee rate test
            createTxRequest.FeeRate = FeeRate.Zero;
            await AssertValidationError(new[] { "FeeRate" }, async () =>
             {
                 tx = await client.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                     createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
             });


            createTxRequest.FeeRate = new FeeRate(5.0m);

            createTxRequest.Destinations[0].Amount = 0.001m;
            createTxRequest.Destinations[0].Destination = nodeAddress.ToString();
            createTxRequest.Destinations[0].SubtractFromAmount = false;
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.CreateOnChainTransactionButDoNotBroadcast(walletId.StoreId, walletId.CryptoCode,
                    createTxRequest, tester.ExplorerClient.Network.NBitcoinNetwork);
            });
            createTxRequest.ProceedWithBroadcast = true;
            var txdata =
                await client.CreateOnChainTransaction(walletId.StoreId, walletId.CryptoCode,
                    createTxRequest);
            Assert.Equal(TransactionStatus.Unconfirmed, txdata.Status);
            Assert.Null(txdata.BlockHeight);
            Assert.Null(txdata.BlockHash);
            Assert.NotNull(await tester.ExplorerClient.GetTransactionAsync(txdata.TransactionHash));

            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.GetOnChainWalletTransaction(walletId.StoreId, walletId.CryptoCode, txdata.TransactionHash.ToString());
            });
            var transaction = await client.GetOnChainWalletTransaction(walletId.StoreId, walletId.CryptoCode, txdata.TransactionHash.ToString());

            // Check skip doesn't crash
            await client.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode, skip: 1);

            Assert.Equal(transaction.TransactionHash, txdata.TransactionHash);
            Assert.Equal(String.Empty, transaction.Comment);
#pragma warning disable CS0612 // Type or member is obsolete
            Assert.Equal(new Dictionary<string, LabelData>(), transaction.Labels);

            // transaction patch tests
            var patchedTransaction = await client.PatchOnChainWalletTransaction(
                walletId.StoreId, walletId.CryptoCode, txdata.TransactionHash.ToString(),
                new PatchOnChainTransactionRequest()
                {
                    Comment = "test comment",
                    Labels = new List<string>
                    {
                        "test label"
                    }
                });
            Assert.Equal("test comment", patchedTransaction.Comment);
            Assert.Equal(
                new Dictionary<string, LabelData>()
                {
                    { "test label", new LabelData(){ Type = "raw", Text = "test label" } }
                }.ToJson(),
                patchedTransaction.Labels.ToJson()
            );
#pragma warning restore CS0612 // Type or member is obsolete
            await AssertHttpError(403, async () =>
            {
                await viewOnlyClient.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode);
            });
            Assert.True(Assert.Single(
                await client.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode,
                    new[] { TransactionStatus.Confirmed })).TransactionHash == utxo.Outpoint.Hash);
            Assert.Contains(
                await client.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode,
                    new[] { TransactionStatus.Unconfirmed }), data => data.TransactionHash == txdata.TransactionHash);
            Assert.Contains(
                await client.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode), data => data.TransactionHash == txdata.TransactionHash);
            Assert.Contains(
                await client.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode, null, "test label"), data => data.TransactionHash == txdata.TransactionHash);

            await tester.WaitForEvent<NewBlockEvent>(async () =>
            {
                await tester.ExplorerNode.GenerateAsync(1);
            }, bevent => bevent.PaymentMethodId == PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));

            Assert.Contains(
                await client.ShowOnChainWalletTransactions(walletId.StoreId, walletId.CryptoCode,
                    new[] { TransactionStatus.Confirmed }), data => data.TransactionHash == txdata.TransactionHash);

        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Lightning", "Lightning")]
        [Trait("Integration", "Integration")]
        public async Task StorePaymentMethodsAPITests()
        {
            using var tester = CreateServerTester();
            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.Unrestricted);
            var viewerOnlyClient = await admin.CreateClient(Policies.CanViewStoreSettings);
            var store = await adminClient.GetStore(admin.StoreId);

            Assert.Empty(await adminClient.GetStorePaymentMethods(store.Id));

            await adminClient.UpdateStorePaymentMethod(admin.StoreId, "BTC-LN", new UpdatePaymentMethodRequest()
            {
                Enabled = true,
                Config = new JObject()
                {
                    {"connectionString", "Internal Node" }
                }
            });

            void VerifyLightning(GenericPaymentMethodData[] methods)
            {
                var m = Assert.Single(methods, m => m.PaymentMethodId == "BTC-LN");
                Assert.Equal("Internal Node", m.Config["internalNodeRef"].Value<string>());
            }

            var methods = await adminClient.GetStorePaymentMethods(store.Id, includeConfig: true);
            Assert.Single(methods);
            VerifyLightning(methods);

            var wallet = await adminClient.GenerateOnChainWallet(store.Id, "BTC", new GenerateOnChainWalletRequest() { });

            void VerifyOnChain(GenericPaymentMethodData[] dictionary)
            {
                var m = Assert.Single(methods, m => m.PaymentMethodId == "BTC-CHAIN");
                var paymentMethodBaseData = Assert.IsType<JObject>(m.Config);
                Assert.Equal(wallet.Config.AccountDerivation, paymentMethodBaseData["accountDerivation"].Value<string>());
            }

            methods = await adminClient.GetStorePaymentMethods(store.Id, includeConfig: true);
            Assert.Equal(2, methods.Length);
            VerifyLightning(methods);
            VerifyOnChain(methods);

            var connStr = tester.GetLightningConnectionString(LightningConnectionType.CLightning, true);
            await adminClient.UpdateStorePaymentMethod(store.Id, "BTC-LN",
                 new UpdatePaymentMethodRequest()
                 {
                     Enabled = true,
                     Config = new JObject()
                     {
                         ["connectionString"] = connStr
                     }
                 });
            await AssertPermissionError("btcpay.store.canmodifystoresettings", () => viewerOnlyClient.GetStorePaymentMethods(store.Id, includeConfig: true));
            methods = await adminClient.GetStorePaymentMethods(store.Id, includeConfig: true);

            Assert.Equal(connStr, methods.FirstOrDefault(m => m.PaymentMethodId == "BTC-LN")?.Config["connectionString"].Value<string>());
            methods = await adminClient.GetStorePaymentMethods(store.Id);
            Assert.Null(methods.FirstOrDefault(m => m.PaymentMethodId == "BTC-LN").Config);
            await this.AssertAPIError("paymentmethod-not-found", () => adminClient.RemoveStorePaymentMethod(store.Id, "LOL"));
            await adminClient.RemoveStorePaymentMethod(store.Id, "BTC-LN");

            // Alternative way of setting the connection string
            await adminClient.UpdateStorePaymentMethod(store.Id, "BTC-LN",
                new UpdatePaymentMethodRequest()
                {
                    Enabled = true,
                    Config = JValue.CreateString("Internal Node")
                });
            methods = await adminClient.GetStorePaymentMethods(store.Id, includeConfig: true);
            Assert.Equal("Internal Node", methods.FirstOrDefault(m => m.PaymentMethodId == "BTC-LN").Config["internalNodeRef"].Value<string>());
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task StoreLightningAddressesAPITests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.Unrestricted);
            var store = await adminClient.GetStore(admin.StoreId);

            Assert.Empty(await adminClient.GetStorePaymentMethods(store.Id));
            var store2 = (await adminClient.CreateStore(new CreateStoreRequest() { Name = "test2" })).Id;
            var address1 = Guid.NewGuid().ToString("n").Substring(0, 8);
            var address2 = Guid.NewGuid().ToString("n").Substring(0, 8);
            var address3 = Guid.NewGuid().ToString("n").Substring(0, 8);

            Assert.Empty(await adminClient.GetStoreLightningAddresses(store.Id));
            Assert.Empty(await adminClient.GetStoreLightningAddresses(store2));
            await adminClient.AddOrUpdateStoreLightningAddress(store.Id, address1, new LightningAddressData());

            await adminClient.AddOrUpdateStoreLightningAddress(store.Id, address1, new LightningAddressData()
            {
                Max = 1
            });
            await AssertAPIError("username-already-used", async () =>
            {
                await adminClient.AddOrUpdateStoreLightningAddress(store2, address1, new LightningAddressData());
            });
            Assert.Equal(1, Assert.Single(await adminClient.GetStoreLightningAddresses(store.Id)).Max);
            Assert.Empty(await adminClient.GetStoreLightningAddresses(store2));

            await adminClient.AddOrUpdateStoreLightningAddress(store2, address2, new LightningAddressData());

            Assert.Single(await adminClient.GetStoreLightningAddresses(store.Id));
            Assert.Single(await adminClient.GetStoreLightningAddresses(store2));
            await AssertHttpError(404, async () =>
            {
                await adminClient.RemoveStoreLightningAddress(store2, address1);
            });
            await adminClient.RemoveStoreLightningAddress(store2, address2);

            Assert.Empty(await adminClient.GetStoreLightningAddresses(store2));

            var store3 = (await adminClient.CreateStore(new CreateStoreRequest { Name = "test3" })).Id;
            Assert.Empty(await adminClient.GetStoreLightningAddresses(store3));
            var metadata = JObject.FromObject(new { test = 123 });
            await adminClient.AddOrUpdateStoreLightningAddress(store3, address3, new LightningAddressData
            {
                InvoiceMetadata = metadata
            });
            var lnAddresses = await adminClient.GetStoreLightningAddresses(store3);
            Assert.Single(lnAddresses);
            Assert.Equal(metadata, lnAddresses[0].InvoiceMetadata);
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task StoreUsersAPITest()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();

            var user = tester.NewAccount();
            await user.GrantAccessAsync(true);

            var client = await user.CreateClient(Policies.CanModifyStoreSettings, Policies.CanModifyServerSettings, Policies.CanModifyProfile);
            await client.UpdateCurrentUser(new UpdateApplicationUserRequest
            {
                Name = "The Admin",
                ImageUrl = "avatar.jpg"
            });

            var roles = await client.GetServerRoles();
            Assert.Equal(4, roles.Count);
#pragma warning disable CS0618
            var ownerRole = roles.Single(data => data.Role == StoreRoles.Owner);
            var managerRole = roles.Single(data => data.Role == StoreRoles.Manager);
            var employeeRole = roles.Single(data => data.Role == StoreRoles.Employee);
            var guestRole = roles.Single(data => data.Role == StoreRoles.Guest);
#pragma warning restore CS0618
            var users = await client.GetStoreUsers(user.StoreId);
            var storeUser = Assert.Single(users);
            Assert.Equal(user.UserId, storeUser.Id);
            Assert.Equal(user.UserId, storeUser.AdditionalData["userId"].ToString());
            Assert.Equal(ownerRole.Id, storeUser.StoreRole);
            Assert.Equal(ownerRole.Id, storeUser.AdditionalData["role"].ToString());
            Assert.Equal(user.Email, storeUser.Email);
            Assert.Equal("The Admin", storeUser.Name);
            Assert.Equal("avatar.jpg", storeUser.ImageUrl);
            var manager = tester.NewAccount();
            await manager.GrantAccessAsync();
            var employee = tester.NewAccount();
            await employee.GrantAccessAsync();
            var guest = tester.NewAccount();
            await guest.GrantAccessAsync();

            var managerClient = await manager.CreateClient(Policies.CanModifyStoreSettings);
            var employeeClient = await employee.CreateClient(Policies.CanModifyStoreSettings);
            var guestClient = await guest.CreateClient(Policies.CanModifyStoreSettings);

            //test no access to api when unrelated to store at all
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await managerClient.GetStore(user.StoreId));
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await managerClient.GetStoreUsers(user.StoreId));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await managerClient.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await managerClient.RemoveStoreUser(user.StoreId, user.UserId));

            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await employeeClient.GetStore(user.StoreId));
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await employeeClient.GetStoreUsers(user.StoreId));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await employeeClient.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await employeeClient.RemoveStoreUser(user.StoreId, user.UserId));

            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await guestClient.GetStore(user.StoreId));
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await guestClient.GetStoreUsers(user.StoreId));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await guestClient.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await guestClient.RemoveStoreUser(user.StoreId, user.UserId));

            // add users to store
            await client.AddStoreUser(user.StoreId, new StoreUserData { StoreRole = managerRole.Id, Id = manager.UserId });
            await client.AddStoreUser(user.StoreId, new StoreUserData { StoreRole = employeeRole.Id, Id = employee.UserId });

            // add with email
            await client.AddStoreUser(user.StoreId, new StoreUserData { StoreRole = guestRole.Id, Id = guest.Email });

            // test unknown user
            await AssertAPIError("user-not-found", async () => await client.AddStoreUser(user.StoreId, new StoreUserData { StoreRole = managerRole.Id, Id = "unknown" }));
            await AssertAPIError("user-not-found", async () => await client.UpdateStoreUser(user.StoreId, "unknown", new StoreUserData { StoreRole = ownerRole.Id }));
            await AssertAPIError("user-not-found", async () => await client.RemoveStoreUser(user.StoreId, "unknown"));

            //test no access to api for employee
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await employeeClient.GetStore(user.StoreId));
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await employeeClient.GetStoreUsers(user.StoreId));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await employeeClient.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await employeeClient.RemoveStoreUser(user.StoreId, user.UserId));

            //test no access to api for guest
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await guestClient.GetStore(user.StoreId));
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await guestClient.GetStoreUsers(user.StoreId));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await guestClient.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await guestClient.RemoveStoreUser(user.StoreId, user.UserId));

            //test access to api for manager
            await managerClient.GetStore(user.StoreId);
            await managerClient.GetStoreUsers(user.StoreId);
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await managerClient.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await managerClient.RemoveStoreUser(user.StoreId, user.UserId));

            // updates
            await client.UpdateStoreUser(user.StoreId, employee.UserId, new StoreUserData { StoreRole = managerRole.Id });
            await employeeClient.GetStore(user.StoreId);
            await AssertAPIError("store-user-role-orphaned", async () => await client.UpdateStoreUser(user.StoreId, user.UserId, new StoreUserData { StoreRole = managerRole.Id }));

            // remove
            await client.RemoveStoreUser(user.StoreId, employee.UserId);
            await AssertHttpError(403, async () => await employeeClient.GetStore(user.StoreId));
            await AssertAPIError("store-user-role-orphaned", async () => await client.RemoveStoreUser(user.StoreId, user.UserId));

            // test duplicate add
            await client.AddStoreUser(user.StoreId, new StoreUserData { StoreRole = ownerRole.Id, Id = employee.UserId });
            await AssertAPIError("duplicate-store-user-role", async () =>
                 await client.AddStoreUser(user.StoreId, new StoreUserData { StoreRole = ownerRole.Id, Id = employee.UserId }));
            await employeeClient.RemoveStoreUser(user.StoreId, user.UserId);

            //test no access to api when unrelated to store at all
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await client.GetStore(user.StoreId));
            await AssertPermissionError(Policies.CanViewStoreSettings, async () => await client.GetStoreUsers(user.StoreId));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await client.AddStoreUser(user.StoreId, new StoreUserData()));
            await AssertPermissionError(Policies.CanModifyStoreSettings, async () => await client.RemoveStoreUser(user.StoreId, user.UserId));

            await AssertAPIError("store-user-role-orphaned", async () => await employeeClient.RemoveStoreUser(user.StoreId, employee.UserId));
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task ServerEmailTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.Unrestricted);
            // validate that clear email settings will not throw an error
            await adminClient.UpdateServerEmailSettings(new ServerEmailSettingsData());

            var data = new ServerEmailSettingsData
            {
                From = "admin@admin.com",
                Login = "admin@admin.com",
                Password = "admin@admin.com",
                Port = 1234,
                Server = "admin.com",
                EnableStoresToUseServerEmailSettings = false
            };
            var actualUpdated = await adminClient.UpdateServerEmailSettings(data);

            var finalEmailSettings = await adminClient.GetServerEmailSettings();
            // email password is masked and not returned from the server once set
            data.Password = null;
            data.PasswordSet = true;

            Assert.Equal(JsonConvert.SerializeObject(finalEmailSettings), JsonConvert.SerializeObject(data));
            Assert.Equal(JsonConvert.SerializeObject(finalEmailSettings), JsonConvert.SerializeObject(actualUpdated));

            // check that email validation works
            await AssertValidationError(new[] { nameof(EmailSettingsData.From) },
                async () => await adminClient.UpdateServerEmailSettings(new ServerEmailSettingsData
                {
                    From = "invalid"
                }));

            // NOTE: This email test fails silently in EmailSender.cs#31, can't test, but leaving for the future as reminder
            //await adminClient.SendEmail(admin.StoreId,
            //    new SendEmailRequest { Body = "lol", Subject = "subj", Email = "to@example.org" });

            // check that clear server email settings works
            await adminClient.UpdateServerEmailSettings(new ServerEmailSettingsData());
            var clearedSettings = await adminClient.GetServerEmailSettings();
            Assert.Equal(JsonConvert.SerializeObject(new ServerEmailSettingsData { PasswordSet = false }), JsonConvert.SerializeObject(clearedSettings));
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task StoreEmailTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.Unrestricted);
            // validate that clear email settings will not throw an error
            await adminClient.UpdateStoreEmailSettings(admin.StoreId, new EmailSettingsData());

            var data = new EmailSettingsData
            {
                From = "admin@admin.com",
                Login = "admin@admin.com",
                Password = "admin@admin.com",
                Port = 1234,
                Server = "admin.com",
            };
            await adminClient.UpdateStoreEmailSettings(admin.StoreId, data);
            var s = await adminClient.GetStoreEmailSettings(admin.StoreId);
            // email password is masked and not returned from the server once set
            data.Password = null;
            data.PasswordSet = true;
            Assert.Equal(JsonConvert.SerializeObject(s), JsonConvert.SerializeObject(data));
            await AssertValidationError(new[] { nameof(EmailSettingsData.From) },
                async () => await adminClient.UpdateStoreEmailSettings(admin.StoreId,
                    new EmailSettingsData { From = "invalid" }));

            // send test email
            await adminClient.SendEmail(admin.StoreId,
                new SendEmailRequest { Body = "lol", Subject = "subj", Email = "to@example.org" });

            // clear store email settings
            await adminClient.UpdateStoreEmailSettings(admin.StoreId, new EmailSettingsData());
            var clearedSettings = await adminClient.GetStoreEmailSettings(admin.StoreId);
            Assert.Equal(JsonConvert.SerializeObject(new EmailSettingsData { PasswordSet = false }), JsonConvert.SerializeObject(clearedSettings));
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task DisabledEnabledUserTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.Unrestricted);

            var newUser = tester.NewAccount();
            await newUser.GrantAccessAsync();
            var newUserClient = await newUser.CreateClient(Policies.Unrestricted);
            Assert.False((await newUserClient.GetCurrentUser()).Disabled);

            Assert.True(await adminClient.LockUser(newUser.UserId, true, CancellationToken.None));
            Assert.True((await adminClient.GetUserByIdOrEmail(newUser.UserId)).Disabled);
            await AssertAPIError("unauthenticated", async () =>
             {
                 await newUserClient.GetCurrentUser();
             });
            var newUserBasicClient = new BTCPayServerClient(newUserClient.Host, newUser.RegisterDetails.Email,
                newUser.RegisterDetails.Password);
            await AssertAPIError("unauthenticated", async () =>
             {
                 await newUserBasicClient.GetCurrentUser();
             });

            Assert.True(await adminClient.LockUser(newUser.UserId, false, CancellationToken.None));
            Assert.False((await adminClient.GetUserByIdOrEmail(newUser.UserId)).Disabled);
            await newUserClient.GetCurrentUser();
            await newUserBasicClient.GetCurrentUser();
            // Twice for good measure
            Assert.True(await adminClient.LockUser(newUser.UserId, false, CancellationToken.None));
            Assert.False((await adminClient.GetUserByIdOrEmail(newUser.UserId)).Disabled);
            await newUserClient.GetCurrentUser();
            await newUserBasicClient.GetCurrentUser();
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task ApproveUserTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);
            var adminClient = await admin.CreateClient(Policies.Unrestricted);
            Assert.False((await adminClient.GetUserByIdOrEmail(admin.UserId)).RequiresApproval);
            Assert.Empty(await adminClient.GetNotifications());

            // require approval
            var settings = tester.PayTester.GetService<SettingsRepository>();
            await settings.UpdateSetting(new PoliciesSettings { LockSubscription = false, RequiresUserApproval = true });

            // new user needs approval
            var unapprovedUser = tester.NewAccount();
            await unapprovedUser.GrantAccessAsync();
            var unapprovedUserBasicAuthClient = await unapprovedUser.CreateClient();
            await AssertAPIError("unauthenticated", async () =>
            {
                await unapprovedUserBasicAuthClient.GetCurrentUser();
            });
            var unapprovedUserApiKeyClient = await unapprovedUser.CreateClient(Policies.Unrestricted);
            await AssertAPIError("unauthenticated", async () =>
            {
                await unapprovedUserApiKeyClient.GetCurrentUser();
            });
            Assert.True((await adminClient.GetUserByIdOrEmail(unapprovedUser.UserId)).RequiresApproval);
            Assert.False((await adminClient.GetUserByIdOrEmail(unapprovedUser.UserId)).Approved);
            Assert.Single(await adminClient.GetNotifications(false));

            // approve
            Assert.True(await adminClient.ApproveUser(unapprovedUser.UserId, true, CancellationToken.None));
            Assert.True((await adminClient.GetUserByIdOrEmail(unapprovedUser.UserId)).Approved);
            Assert.True((await unapprovedUserApiKeyClient.GetCurrentUser()).Approved);
            Assert.True((await unapprovedUserBasicAuthClient.GetCurrentUser()).Approved);
            var err = await AssertAPIError("invalid-state", async () =>
            {
                await adminClient.ApproveUser(unapprovedUser.UserId, true, CancellationToken.None);
            });
            Assert.Equal("User is already approved", err.APIError.Message);

            // un-approve
            Assert.True(await adminClient.ApproveUser(unapprovedUser.UserId, false, CancellationToken.None));
            Assert.False((await adminClient.GetUserByIdOrEmail(unapprovedUser.UserId)).Approved);
            await AssertAPIError("unauthenticated", async () =>
            {
                await unapprovedUserApiKeyClient.GetCurrentUser();
            });
            await AssertAPIError("unauthenticated", async () =>
            {
                await unapprovedUserBasicAuthClient.GetCurrentUser();
            });
            err = await AssertAPIError("invalid-state", async () =>
            {
                await adminClient.ApproveUser(unapprovedUser.UserId, false, CancellationToken.None);
            });
            Assert.Equal("User is already unapproved", err.APIError.Message);

            // reset policies to not require approval
            await settings.UpdateSetting(new PoliciesSettings { LockSubscription = false, RequiresUserApproval = false });

            // new user does not need approval
            var newUser = tester.NewAccount();
            await newUser.GrantAccessAsync();
            var newUserBasicAuthClient = await newUser.CreateClient();
            var newUserApiKeyClient = await newUser.CreateClient(Policies.Unrestricted);
            Assert.False((await newUserApiKeyClient.GetCurrentUser()).RequiresApproval);
            Assert.False((await newUserApiKeyClient.GetCurrentUser()).Approved);
            Assert.False((await newUserBasicAuthClient.GetCurrentUser()).RequiresApproval);
            Assert.False((await newUserBasicAuthClient.GetCurrentUser()).Approved);
            Assert.Single(await adminClient.GetNotifications(false));

            // try unapproving user which does not have the RequiresApproval flag
            err = await AssertAPIError("invalid-state", async () =>
            {
                await adminClient.ApproveUser(newUser.UserId, false, CancellationToken.None);
            });
            Assert.Equal("Unapproving user failed: No approval required", err.APIError.Message);
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        [Trait("Lightning", "Lightning")]
        public async Task CanUseLNPayoutProcessor()
        {
            LightningPendingPayoutListener.SecondsDelay = 0;
            using var tester = CreateServerTester();

            tester.ActivateLightning();
            await tester.StartAsync();
            await tester.EnsureChannelsSetup();

            var admin = tester.NewAccount();

            await admin.GrantAccessAsync(true);

            var adminClient = await admin.CreateClient(Policies.Unrestricted);
            admin.RegisterLightningNode("BTC", LightningConnectionType.CLightning);
            var payoutAmount = LightMoney.Satoshis(1000);
            var inv = await tester.MerchantLnd.Client.CreateInvoice(payoutAmount, "Donation to merchant", TimeSpan.FromHours(1), default);
            var resp = await tester.CustomerLightningD.Pay(inv.BOLT11);
            Assert.Equal(PayResult.Ok, resp.Result);

            var ppService = tester.PayTester.GetService<HostedServices.PullPaymentHostedService>();
            tester.PayTester.GetService<BTCPayNetworkJsonSerializerSettings>();
            var store = tester.PayTester.GetService<StoreRepository>();
            var dbContextFactory = tester.PayTester.GetService<Data.ApplicationDbContextFactory>();

            Assert.True(await store.InternalNodePayoutAuthorized(admin.StoreId));
            Assert.False(await store.InternalNodePayoutAuthorized("blah"));
            await admin.MakeAdmin(false);
            Assert.False(await store.InternalNodePayoutAuthorized(admin.StoreId));
            await admin.MakeAdmin(true);

            var customerInvoice = await tester.CustomerLightningD.CreateInvoice(LightMoney.FromUnit(10, LightMoneyUnit.Satoshi),
                Guid.NewGuid().ToString(), TimeSpan.FromDays(40));
            var payout = await adminClient.CreatePayout(admin.StoreId,
                new CreatePayoutThroughStoreRequest()
                {
                    Approved = true,
                    PayoutMethodId = "BTC_LightningNetwork",
                    Destination = customerInvoice.BOLT11
                });
            Assert.Equal(payout.Metadata.ToString(), new JObject().ToString()); //empty
            Assert.Empty(await adminClient.GetStoreLightningAutomatedPayoutProcessors(admin.StoreId, "BTC_LightningNetwork"));
            await adminClient.UpdateStoreLightningAutomatedPayoutProcessors(admin.StoreId, "BTC_LightningNetwork",
                new LightningAutomatedPayoutSettings() { IntervalSeconds = TimeSpan.FromSeconds(600) });
            Assert.Equal(600, Assert.Single(await adminClient.GetStoreLightningAutomatedPayoutProcessors(admin.StoreId, "BTC_LightningNetwork")).IntervalSeconds.TotalSeconds);
            await TestUtils.EventuallyAsync(async () =>
            {
                var payoutC =
                    (await adminClient.GetStorePayouts(admin.StoreId, false)).SingleOrDefault(data => data.Id == payout.Id);
                Assert.Equal(PayoutState.Completed, payoutC?.State);
            });

            payout = await adminClient.CreatePayout(admin.StoreId,
                new CreatePayoutThroughStoreRequest()
                {
                    Approved = true,
                    PayoutMethodId = "BTC",
                    Destination = (await tester.ExplorerNode.GetNewAddressAsync()).ToString(),
                    Amount = 0.0001m,
                    Metadata = JObject.FromObject(new
                    {
                        source = "apitest",
                        sourceLink = "https://chocolate.com"
                    })
                });
            Assert.Equal(payout.Metadata.ToString(), JObject.FromObject(new
            {
                source = "apitest",
                sourceLink = "https://chocolate.com"
            }).ToString());

            payout =
                (await adminClient.GetStorePayouts(admin.StoreId, false)).Single(data => data.Id == payout.Id);

            Assert.Equal(payout.Metadata.ToString(), JObject.FromObject(new
            {
                source = "apitest",
                sourceLink = "https://chocolate.com"
            }).ToString());

            customerInvoice = await tester.CustomerLightningD.CreateInvoice(LightMoney.FromUnit(10, LightMoneyUnit.Satoshi),
                Guid.NewGuid().ToString(), TimeSpan.FromDays(40));
            var payout2 = await adminClient.CreatePayout(admin.StoreId,
                new CreatePayoutThroughStoreRequest()
                {
                    Approved = true,
                    Amount = new Money(100, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC),
                    PayoutMethodId = "BTC_LightningNetwork",
                    Destination = customerInvoice.BOLT11
                });
            Assert.Equal(payout2.OriginalAmount, new Money(100, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC));

            // Checking if we can disable a payout...
            var allLNPayouts = await ppService.GetPayouts(new ()
            {
                PayoutIds = new[] { payout2.Id },
                Processor = LightningAutomatedPayoutSenderFactory.ProcessorName
            });
            Assert.NotEmpty(allLNPayouts);
            var b = JsonConvert.DeserializeObject<Data.PayoutBlob>(allLNPayouts[0].Blob);
            b.DisableProcessor(LightningAutomatedPayoutSenderFactory.ProcessorName);
            Assert.Equal(1, b.IncrementErrorCount());
            Assert.Equal(2, b.IncrementErrorCount());
            allLNPayouts[0].Blob = JsonConvert.SerializeObject(b);
            Assert.Equal(3, JsonConvert.DeserializeObject<Data.PayoutBlob>(allLNPayouts[0].Blob).IncrementErrorCount());
            using var ctx = dbContextFactory.CreateContext();
            var p = ctx.Payouts.Find(allLNPayouts[0].Id);
            p.Blob = allLNPayouts[0].Blob;
            await ctx.SaveChangesAsync();
            var allLNPayouts2 = await ppService.GetPayouts(new()
            {
                PayoutIds = new[] { payout2.Id },
                Processor = LightningAutomatedPayoutSenderFactory.ProcessorName
            });
            Assert.DoesNotContain(allLNPayouts[0].Id, allLNPayouts2.Select(a => a.Id));
            allLNPayouts2 = await ppService.GetPayouts(new()
            {
                PayoutIds = new[] { payout2.Id },
                Processor = "hello"
            });
            Assert.Contains(allLNPayouts[0].Id, allLNPayouts2.Select(a => a.Id));
        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task CanUsePayoutProcessorsThroughAPI()
        {

            using var tester = CreateServerTester();
            await tester.StartAsync();

            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);

            var adminClient = await admin.CreateClient(Policies.Unrestricted);

            var registeredProcessors = await adminClient.GetPayoutProcessors();
            Assert.Equal(2, registeredProcessors.Count());
            await adminClient.GenerateOnChainWallet(admin.StoreId, "BTC", new GenerateOnChainWalletRequest()
            {
                SavePrivateKeys = true
            });

            await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                Amount = 0.0001m,
                Approved = true,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });

            var notApprovedPayoutWithoutPullPayment = await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                Amount = 0.00001m,
                Approved = false,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });

            var pullPayment = await adminClient.CreatePullPayment(admin.StoreId, new CreatePullPaymentRequest()
            {
                Amount = 100,
                Currency = "USD",
                Name = "pull payment",
                PayoutMethods = new[] { "BTC" }
            });

            var notapprovedPayoutWithPullPayment = await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                PullPaymentId = pullPayment.Id,
                Amount = 10,
                Approved = false,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });
            await adminClient.ApprovePayout(admin.StoreId, notapprovedPayoutWithPullPayment.Id,
                new ApprovePayoutRequest() { });

            var payouts = await adminClient.GetStorePayouts(admin.StoreId);

            Assert.Equal(3, payouts.Length);
            Assert.Single(payouts, data => data.State == PayoutState.AwaitingApproval);
            await adminClient.ApprovePayout(admin.StoreId, notApprovedPayoutWithoutPullPayment.Id,
                new ApprovePayoutRequest() { });


            payouts = await adminClient.GetStorePayouts(admin.StoreId);

            Assert.Equal(3, payouts.Length);
            Assert.DoesNotContain(payouts, data => data.State == PayoutState.AwaitingApproval);
            Assert.DoesNotContain(payouts, data => data.PayoutAmount is null);

            Assert.Empty(await adminClient.ShowOnChainWalletTransactions(admin.StoreId, "BTC"));


            Assert.Empty(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC"));
            Assert.Empty(await adminClient.GetPayoutProcessors(admin.StoreId));

            await adminClient.UpdateStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC",
                new OnChainAutomatedPayoutSettings() { IntervalSeconds = TimeSpan.FromSeconds(3600) });
            Assert.Equal(3600, Assert.Single(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC")).IntervalSeconds.TotalSeconds);

            var tpGen = Assert.Single(await adminClient.GetPayoutProcessors(admin.StoreId));
            Assert.Equal("BTC-CHAIN", Assert.Single(tpGen.PayoutMethods));
            //still too poor to process any payouts
            Assert.Empty(await adminClient.ShowOnChainWalletTransactions(admin.StoreId, "BTC"));


            await adminClient.RemovePayoutProcessor(admin.StoreId, tpGen.Name, tpGen.PayoutMethods.First());

            Assert.Empty(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC"));
            Assert.Empty(await adminClient.GetPayoutProcessors(admin.StoreId));

            // Send just enough money to cover the smallest of the payouts.
            var fee = (await tester.PayTester.GetService<IFeeProviderFactory>().CreateFeeProvider(tester.DefaultNetwork).GetFeeRateAsync(100)).GetFee(150);
            await tester.ExplorerNode.SendToAddressAsync(BitcoinAddress.Create((await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
                tester.ExplorerClient.Network.NBitcoinNetwork), Money.Coins(0.00001m) + fee);
            await tester.ExplorerNode.GenerateAsync(1);
            await TestUtils.EventuallyAsync(async () =>
            {
                Assert.Single(await adminClient.ShowOnChainWalletTransactions(admin.StoreId, "BTC"));

                payouts = await adminClient.GetStorePayouts(admin.StoreId);
                Assert.Equal(3, payouts.Length);
            });
            await adminClient.UpdateStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC",
                new OnChainAutomatedPayoutSettings() { IntervalSeconds = TimeSpan.FromSeconds(600), FeeBlockTarget = 1000 });
            Assert.Equal(600, Assert.Single(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC")).IntervalSeconds.TotalSeconds);

            await TestUtils.EventuallyAsync(async () =>
            {
                Assert.Equal(2, (await adminClient.ShowOnChainWalletTransactions(admin.StoreId, "BTC")).Count());
                payouts = await adminClient.GetStorePayouts(admin.StoreId);
                Assert.Single(payouts, data => data.State == PayoutState.InProgress);
            });

            uint256 txid = null;
            await tester.WaitForEvent<NewOnChainTransactionEvent>(async () =>
            {
                txid = await tester.ExplorerNode.SendToAddressAsync(BitcoinAddress.Create((await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
                    tester.ExplorerClient.Network.NBitcoinNetwork), Money.Coins(0.01m) + fee);
            }, correctEvent: ev => ev.NewTransactionEvent.TransactionData.TransactionHash == txid);
            await tester.PayTester.GetService<PayoutProcessorService>().Restart(new PayoutProcessorService.PayoutProcessorQuery(admin.StoreId, Payouts.PayoutMethodId.Parse("BTC")));
            await TestUtils.EventuallyAsync(async () =>
            {
                Assert.Equal(4, (await adminClient.ShowOnChainWalletTransactions(admin.StoreId, "BTC")).Count());
                payouts = await adminClient.GetStorePayouts(admin.StoreId);
                Assert.DoesNotContain(payouts, data => data.State != PayoutState.InProgress);
            });

            // settings that were added later
            var settings =
                Assert.Single(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC"));
            Assert.False(settings.ProcessNewPayoutsInstantly);
            Assert.Equal(0m, settings.Threshold);

            //let's use the ProcessNewPayoutsInstantly so that it will trigger instantly

            settings.IntervalSeconds = TimeSpan.FromDays(1);
            settings.ProcessNewPayoutsInstantly = true;

            await tester.WaitForEvent<NewOnChainTransactionEvent>(async () =>
            {
                txid = await tester.ExplorerNode.SendToAddressAsync(BitcoinAddress.Create((await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
                    tester.ExplorerClient.Network.NBitcoinNetwork), Money.Coins(1m) + fee);
            }, correctEvent: ev => ev.NewTransactionEvent.TransactionData.TransactionHash == txid);

            await adminClient.UpdateStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC", settings);
            settings =
                Assert.Single(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC"));
            Assert.True(settings.ProcessNewPayoutsInstantly);

            var pluginHookService = tester.PayTester.GetService<IPluginHookService>();
            var beforeHookTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var afterHookTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TestLogs.LogInformation("Adding hook...");
            pluginHookService.ActionInvoked += (sender, tuple) =>
            {
                switch (tuple.hook)
                {
                    case "before-automated-payout-processing":
                        beforeHookTcs.TrySetResult();
                        var bd = (BeforePayoutActionData)tuple.args;
                        foreach (var p in bd.Payouts)
                        {
                            TestLogs.LogInformation("Before Processed: " + p.Id);
                        }
                        break;
                    case "after-automated-payout-processing":
                        afterHookTcs.TrySetResult();
                        var ad = (AfterPayoutActionData)tuple.args;
                        foreach (var p in ad.Payouts)
                        {
                            TestLogs.LogInformation("After Processed: " + p.Id);
                        }
                        break;
                }
            };
            var payoutThatShouldBeProcessedStraightAway = await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                PullPaymentId = pullPayment.Id,
                Amount = 0.5m,
                Approved = true,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });
            TestLogs.LogInformation("Waiting before hook...");
            await beforeHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestLogs.LogInformation("Waiting before after...");
            await afterHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            payouts = await adminClient.GetStorePayouts(admin.StoreId);
            try
            {
                Assert.Single(payouts, data => data.State == PayoutState.InProgress && data.Id == payoutThatShouldBeProcessedStraightAway.Id);
            }
            catch (SingleException)
            {
                TestLogs.LogInformation("Debugging flaky test...");
                TestLogs.LogInformation("payoutThatShouldBeProcessedStraightAway: " + payoutThatShouldBeProcessedStraightAway.Id);
                foreach (var p in payouts)
                {
                    TestLogs.LogInformation("Payout Id: " + p.Id);
                    TestLogs.LogInformation("Payout State: " + p.State);
                }
                throw;
            }

            beforeHookTcs = new TaskCompletionSource();
            afterHookTcs = new TaskCompletionSource();
            //let's test the threshold limiter
            settings.Threshold = 0.5m;
            await adminClient.UpdateStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC", settings);

            //quick test: when updating processor, it processes instantly
            await beforeHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await afterHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            settings =
                Assert.Single(await adminClient.GetStoreOnChainAutomatedPayoutProcessors(admin.StoreId, "BTC"));
            Assert.Equal(0.5m, settings.Threshold);

            //create a payout that should not be processed straight away due to threshold

            beforeHookTcs = new TaskCompletionSource();
            afterHookTcs = new TaskCompletionSource();
            var payoutThatShouldNotBeProcessedStraightAway = await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                Amount = 0.1m,
                Approved = true,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });

            await beforeHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await afterHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            payouts = await adminClient.GetStorePayouts(admin.StoreId);
            Assert.Single(payouts, data => data.State == PayoutState.AwaitingPayment && data.Id == payoutThatShouldNotBeProcessedStraightAway.Id);

            beforeHookTcs = new TaskCompletionSource();
            afterHookTcs = new TaskCompletionSource();
            var payoutThatShouldNotBeProcessedStraightAway2 = await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                Amount = 0.3m,
                Approved = true,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });

            await beforeHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await afterHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            payouts = await adminClient.GetStorePayouts(admin.StoreId);
            Assert.Equal(2, payouts.Count(data => data.State == PayoutState.AwaitingPayment &&
                                                  (data.Id == payoutThatShouldNotBeProcessedStraightAway.Id || data.Id == payoutThatShouldNotBeProcessedStraightAway2.Id)));

            beforeHookTcs = new TaskCompletionSource();
            afterHookTcs = new TaskCompletionSource();
            await adminClient.CreatePayout(admin.StoreId, new CreatePayoutThroughStoreRequest()
            {
                Amount = 0.3m,
                Approved = true,
                PayoutMethodId = "BTC",
                Destination = (await adminClient.GetOnChainWalletReceiveAddress(admin.StoreId, "BTC", true)).Address,
            });

            await beforeHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await afterHookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            payouts = await adminClient.GetStorePayouts(admin.StoreId);
            Assert.DoesNotContain(payouts, data => data.State != PayoutState.InProgress);

        }

        [Fact(Timeout = 60 * 2 * 1000)]
        [Trait("Integration", "Integration")]
        public async Task CanUseWalletObjectsAPI()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();

            var admin = tester.NewAccount();
            await admin.GrantAccessAsync(true);

            var client = await admin.CreateClient(Policies.Unrestricted);

            Assert.Empty(await client.GetOnChainWalletObjects(admin.StoreId, "BTC"));
            var test = new OnChainWalletObjectId("test", "test");
            Assert.NotNull(await client.AddOrUpdateOnChainWalletObject(admin.StoreId, "BTC", new AddOnChainWalletObjectRequest(test.Type, test.Id)));

            Assert.Single(await client.GetOnChainWalletObjects(admin.StoreId, "BTC"));
            Assert.NotNull(await client.GetOnChainWalletObject(admin.StoreId, "BTC", test));
            Assert.Null(await client.GetOnChainWalletObject(admin.StoreId, "BTC", new OnChainWalletObjectId("test-wrong", "test")));
            Assert.Null(await client.GetOnChainWalletObject(admin.StoreId, "BTC", new OnChainWalletObjectId("test", "test-wrong")));

            await client.RemoveOnChainWalletObject(admin.StoreId, "BTC", new OnChainWalletObjectId("test", "test"));

            Assert.Empty(await client.GetOnChainWalletObjects(admin.StoreId, "BTC"));

            var test1 = new OnChainWalletObjectId("test", "test1");
            var test2 = new OnChainWalletObjectId("test", "test2");
            await client.AddOrUpdateOnChainWalletObject(admin.StoreId, "BTC", new AddOnChainWalletObjectRequest(test.Type, test.Id));
            // Those links don't exists
            await AssertAPIError("wallet-object-not-found", () => client.AddOrUpdateOnChainWalletLink(admin.StoreId, "BTC", test, new AddOnChainWalletObjectLinkRequest(test1.Type, test1.Id)));
            await AssertAPIError("wallet-object-not-found", () => client.AddOrUpdateOnChainWalletLink(admin.StoreId, "BTC", test, new AddOnChainWalletObjectLinkRequest(test2.Type, test2.Id)));

            Assert.Single(await client.GetOnChainWalletObjects(admin.StoreId, "BTC"));

            await client.AddOrUpdateOnChainWalletObject(admin.StoreId, "BTC", new AddOnChainWalletObjectRequest(test1.Type, test1.Id));
            await client.AddOrUpdateOnChainWalletObject(admin.StoreId, "BTC", new AddOnChainWalletObjectRequest(test2.Type, test2.Id));

            await client.AddOrUpdateOnChainWalletLink(admin.StoreId, "BTC", test, new AddOnChainWalletObjectLinkRequest(test1.Type, test1.Id));
            await client.AddOrUpdateOnChainWalletLink(admin.StoreId, "BTC", test, new AddOnChainWalletObjectLinkRequest(test2.Type, test2.Id));

            var objs = await client.GetOnChainWalletObjects(admin.StoreId, "BTC");
            Assert.Equal(3, objs.Length);
            var middleObj = objs.Single(data => data.Id == "test" && data.Type == "test");
            Assert.Equal(2, middleObj.Links.Length);
            Assert.Contains("test1", middleObj.Links.Select(l => l.Id));
            Assert.Contains("test2", middleObj.Links.Select(l => l.Id));

            var test1Obj = objs.Single(data => data.Id == "test1" && data.Type == "test");
            var test2Obj = objs.Single(data => data.Id == "test2" && data.Type == "test");
            Assert.Single(test1Obj.Links.Select(l => l.Id), l => l == "test");
            Assert.Single(test2Obj.Links.Select(l => l.Id), l => l == "test");

            await client.RemoveOnChainWalletLinks(admin.StoreId, "BTC",
                test1,
                test);

            var testObj = await client.GetOnChainWalletObject(admin.StoreId, "BTC", test);
            Assert.Single(testObj.Links.Select(l => l.Id), l => l == "test2");
            Assert.Single(testObj.Links);
            test1Obj = await client.GetOnChainWalletObject(admin.StoreId, "BTC", test1);
            Assert.Empty(test1Obj.Links);

            await client.AddOrUpdateOnChainWalletLink(admin.StoreId, "BTC",
                test1,
                new AddOnChainWalletObjectLinkRequest(test.Type, test.Id) { Data = new JObject() { ["testData"] = "lol" } });

            // Add some data to test1
            await client.AddOrUpdateOnChainWalletObject(admin.StoreId, "BTC", new AddOnChainWalletObjectRequest() { Type = test1.Type, Id = test1.Id, Data = new JObject() { ["testData"] = "test1" } });

            // Create a new type
            await client.AddOrUpdateOnChainWalletObject(admin.StoreId, "BTC", new AddOnChainWalletObjectRequest() { Type = "newtype", Id = test1.Id });

            testObj = await client.GetOnChainWalletObject(admin.StoreId, "BTC", test);
            Assert.Single(testObj.Links, l => l.Id == "test1" && l.LinkData["testData"]?.Value<string>() == "lol");
            Assert.Single(testObj.Links, l => l.Id == "test1" && l.ObjectData["testData"]?.Value<string>() == "test1");
            testObj = await client.GetOnChainWalletObject(admin.StoreId, "BTC", test, false);
            Assert.Single(testObj.Links, l => l.Id == "test1" && l.LinkData["testData"]?.Value<string>() == "lol");
            Assert.Single(testObj.Links, l => l.Id == "test1" && l.ObjectData is null);

            async Task TestWalletRepository()
            {
                // We should have 4 nodes, two `test` type and one `newtype`
                // Only the node `test` `test` is connected to `test1`
                var wid = new WalletId(admin.StoreId, "BTC");
                var repo = tester.PayTester.GetService<WalletRepository>();
                var allObjects = await repo.GetWalletObjects(new(wid));
                var allObjectsNoWallet = await repo.GetWalletObjects((new()));
                var allObjectsNoWalletAndType = await repo.GetWalletObjects((new() { Type = "test" }));
                var allTests = await repo.GetWalletObjects((new(wid, "test")));
                var twoTests2 = await repo.GetWalletObjects((new(wid, "test", new[] { "test1", "test2", "test-unk" })));
                var oneTest = await repo.GetWalletObjects((new(wid, "test", new[] { "test" })));
                var oneTestWithoutData = await repo.GetWalletObjects((new(wid, "test", new[] { "test" }) { IncludeNeighbours = false }));
                var idsTypes = await repo.GetWalletObjects((new(wid) { TypesIds = new[] { new ObjectTypeId("test", "test1"), new ObjectTypeId("test", "test2") }}));

                Assert.Equal(4, allObjects.Count);
                // We are reusing a db in this test, as such we may have other wallets here.
                Assert.True(allObjectsNoWallet.Count >= 4);
                Assert.True(allObjectsNoWalletAndType.Count >= 3);
                Assert.Equal(3, allTests.Count);
                Assert.Equal(2, twoTests2.Count);
                Assert.Single(oneTest);
                Assert.NotNull(oneTest.First().Value.GetNeighbours().Select(n => n.Data).FirstOrDefault());
                Assert.Single(oneTestWithoutData);
                Assert.Null(oneTestWithoutData.First().Value.GetNeighbours().Select(n => n.Data).FirstOrDefault());
                Assert.Equal(2, idsTypes.Count);
            }
            await TestWalletRepository();

            {
                var allObjects = await client.GetOnChainWalletObjects(admin.StoreId, "BTC");
                var allTests = await client.GetOnChainWalletObjects(admin.StoreId, "BTC", new GetWalletObjectsRequest() { Type = "test" });
                var twoTests2 = await client.GetOnChainWalletObjects(admin.StoreId, "BTC", new GetWalletObjectsRequest() { Type = "test", Ids = new[] { "test1", "test2", "test-unk" } });
                var oneTest = await client.GetOnChainWalletObjects(admin.StoreId, "BTC", new GetWalletObjectsRequest() { Type = "test", Ids = new[] { "test" } });
                var oneTestWithoutData = await client.GetOnChainWalletObjects(admin.StoreId, "BTC", new GetWalletObjectsRequest() { Type = "test", Ids = new[] { "test" }, IncludeNeighbourData = false });

                Assert.Equal(4, allObjects.Length);
                Assert.Equal(3, allTests.Length);
                Assert.Equal(2, twoTests2.Length);
                Assert.Single(oneTest);
                Assert.NotNull(oneTest.First().Links.Select(n => n.ObjectData).FirstOrDefault());
                Assert.Single(oneTestWithoutData);
                Assert.Null(oneTestWithoutData.First().Links.Select(n => n.ObjectData).FirstOrDefault());
            }
        }
        [Fact(Timeout = TestTimeout)]
        [Trait("Integration", "Integration")]
        public async Task StoreRateConfigTests()
        {
            using var tester = CreateServerTester();
            await tester.StartAsync();
            var unauthClient = new BTCPayServerClient(tester.PayTester.ServerUri);
            await AssertHttpError(401, async () => await unauthClient.GetRateSources());

            var user = tester.NewAccount();
            await user.GrantAccessAsync();
            var clientBasic = await user.CreateClient();
            Assert.NotEmpty(await clientBasic.GetRateSources());
            var config = await clientBasic.GetStoreRateConfiguration(user.StoreId);
            Assert.NotNull(config);
            Assert.False(config.IsCustomScript);
            Assert.Equal("X_X = coingecko(X_X);", config.EffectiveScript);
            Assert.Equal("coingecko", config.PreferredSource);

            Assert.Equal(0.9m,
                Assert.Single(await clientBasic.PreviewUpdateStoreRateConfiguration(user.StoreId,
                    new StoreRateConfiguration() { IsCustomScript = true, EffectiveScript = "BTC_XYZ = 1;", Spread = 10m, },
                    new[] { "BTC_XYZ" })).Rate);

            Assert.True((await clientBasic.UpdateStoreRateConfiguration(user.StoreId,
                    new StoreRateConfiguration() { IsCustomScript = true, EffectiveScript = "BTC_XYZ = 1", Spread = 10m, }))
                .IsCustomScript);

            Assert.Equal(0.9m,
                Assert.Single(await clientBasic.GetStoreRates(user.StoreId, new[] { "BTC_XYZ" })).Rate);

            config = await clientBasic.GetStoreRateConfiguration(user.StoreId);
            Assert.NotNull(config);
            Assert.NotNull(config.EffectiveScript);
            Assert.Equal("BTC_XYZ = 1;", config.EffectiveScript);
            Assert.Equal(10m, config.Spread);
            Assert.Null(config.PreferredSource);

            Assert.NotNull((await clientBasic.GetStoreRateConfiguration(user.StoreId)).EffectiveScript);
            Assert.NotNull((await clientBasic.UpdateStoreRateConfiguration(user.StoreId,
                    new StoreRateConfiguration() { IsCustomScript = false, PreferredSource = "coingecko" }))
                .PreferredSource);

            config = await clientBasic.GetStoreRateConfiguration(user.StoreId);
            Assert.Equal("X_X = coingecko(X_X);", config.EffectiveScript);

            await AssertValidationError(new[] { "EffectiveScript" }, () =>
            clientBasic.UpdateStoreRateConfiguration(user.StoreId, new StoreRateConfiguration() { IsCustomScript = false, EffectiveScript = "BTC_XYZ = 1;" }));

            await AssertValidationError(new[] { "EffectiveScript" }, () =>
clientBasic.UpdateStoreRateConfiguration(user.StoreId, new StoreRateConfiguration() { IsCustomScript = true, EffectiveScript = "BTC_XYZ rg8w*# 1;" }));
            await AssertValidationError(new[] { "PreferredSource" }, () =>
clientBasic.UpdateStoreRateConfiguration(user.StoreId, new StoreRateConfiguration() { IsCustomScript = true, EffectiveScript = "", PreferredSource = "coingecko" }));

            await AssertValidationError(new[] { "PreferredSource", "Spread" }, () =>
clientBasic.UpdateStoreRateConfiguration(user.StoreId, new StoreRateConfiguration() { IsCustomScript = false, PreferredSource = "coingeckoOOO", Spread = -1m }));

            await AssertValidationError(new[] { "currencyPair" }, () =>
clientBasic.PreviewUpdateStoreRateConfiguration(user.StoreId, new StoreRateConfiguration() { IsCustomScript = false, PreferredSource = "coingecko" }, new[] { "BTCUSDUSDBTC" }));
            await AssertValidationError(new[] { "PreferredSource", "currencyPair" }, () =>
clientBasic.PreviewUpdateStoreRateConfiguration(user.StoreId, new StoreRateConfiguration() { IsCustomScript = false, PreferredSource = "coingeckoOOO" }, new[] { "BTCUSDUSDBTC" }));
        }
    }
}
