using CoreSync.SqlServer;
using CoreSync.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace CoreSync.Tests
{
    [TestClass]
    public class IntegrationTests
    {
        public static string ConnectionString => Environment.GetEnvironmentVariable("CORE-SYNC_CONNECTION_STRING") ??
                                                 throw new ArgumentException(
                                                     "Set CORE-SYNC_CONNECTION_STRING environmental variable containing connection string to Sql Server");

        [TestMethod]
        public async Task Test1()
        {
            using (var dbLocal = new SampleDbContext(ConnectionString + ";Initial Catalog=Test1_Local"))
            using (var dbRemote = new SampleDbContext(ConnectionString + ";Initial Catalog=Test1_Remote"))
            {
                await dbLocal.Database.EnsureDeletedAsync();
                await dbRemote.Database.EnsureDeletedAsync();

                await dbLocal.Database.MigrateAsync();
                await dbRemote.Database.MigrateAsync();

                var remoteConfigurationBuilder =
                    new SqlSyncConfigurationBuilder(dbRemote.ConnectionString)
                    .Table("Users")
                    .Table("Posts")
                    .Table("Comments");

                var remoteSyncProvider = new SqlSyncProvider(remoteConfigurationBuilder.Configuration);

                var localConfigurationBuilder =
                    new SqlSyncConfigurationBuilder(dbLocal.ConnectionString)
                    .Table("Users")
                    .Table("Posts")
                    .Table("Comments");

                var localSyncProvider = new SqlSyncProvider(localConfigurationBuilder.Configuration);

                var initialSet = await remoteSyncProvider.GetInitialSetAsync();

                Assert.IsNotNull(initialSet);
                Assert.IsNotNull(initialSet.Items);
                Assert.AreEqual(0, initialSet.Items.Count);
                Assert.AreEqual(0, ((SqlSyncAnchor)initialSet.Anchor).Version);

                var changeSet = await remoteSyncProvider.GetIncreamentalChangesAsync(initialSet.Anchor);
                Assert.IsNotNull(changeSet);
                Assert.IsNotNull(changeSet.Items);
                Assert.AreEqual(0, changeSet.Items.Count);
                Assert.AreEqual(0, ((SqlSyncAnchor)changeSet.Anchor).Version);

                var newUser = new User() { Email = "myemail@test.com", Name = "User1", Created = DateTime.Now };
                dbRemote.Users.Add(newUser);
                await dbRemote.SaveChangesAsync();

                var changeSetAfterUserAdd = await remoteSyncProvider.GetIncreamentalChangesAsync(initialSet.Anchor);
                Assert.IsNotNull(changeSetAfterUserAdd);
                Assert.IsNotNull(changeSetAfterUserAdd.Items);
                Assert.AreEqual(1, changeSetAfterUserAdd.Items.Count);
                Assert.AreEqual(1, ((SqlSyncAnchor)changeSetAfterUserAdd.Anchor).Version);
                Assert.AreEqual(ChangeType.Insert, changeSetAfterUserAdd.Items[0].ChangeType);
                Assert.AreEqual(newUser.Email, changeSetAfterUserAdd.Items[0].Values["Email"]);
                Assert.AreEqual(newUser.Name, changeSetAfterUserAdd.Items[0].Values["Name"]);

                var finalAnchor = await localSyncProvider.ApplyChangesAsync(new SqlSyncChangeSet((SqlSyncAnchor)initialSet.Anchor, changeSetAfterUserAdd.Items));
                Assert.IsNotNull(finalAnchor);
                Assert.AreEqual(1, ((SqlSyncAnchor)finalAnchor).Version);

                //try to apply same changeset result in an exception
                var exception = await Assert.ThrowsExceptionAsync<InvalidSyncOperationException>(()=> localSyncProvider.ApplyChangesAsync(new SqlSyncChangeSet((SqlSyncAnchor)initialSet.Anchor, changeSetAfterUserAdd.Items)));
                Assert.IsNotNull(exception);
                Assert.AreEqual(((SqlSyncAnchor)finalAnchor).Version, exception.CandidateAnchor.Version);

                newUser.Created = new DateTime(2018, 1, 1);
                await dbRemote.SaveChangesAsync();

                {
                    //after saved changes version should be updated as well at 2
                    var changeSetAfterUserEdit = await remoteSyncProvider.GetIncreamentalChangesAsync(initialSet.Anchor);
                    Assert.IsNotNull(changeSetAfterUserEdit);
                    Assert.IsNotNull(changeSetAfterUserEdit.Items);
                    Assert.AreEqual(1, changeSetAfterUserEdit.Items.Count);
                    Assert.AreEqual(2, ((SqlSyncAnchor)changeSetAfterUserEdit.Anchor).Version);
                    Assert.AreEqual(newUser.Email, changeSetAfterUserEdit.Items[0].Values["Email"]);
                    Assert.AreEqual(newUser.Name, changeSetAfterUserEdit.Items[0].Values["Name"]);
                    Assert.AreEqual(newUser.Created, changeSetAfterUserEdit.Items[0].Values["Created"]);
                }

                {
                    //now let's change same record in local database and try to apply changes to remote db
                    //this should result in a conflict
                    var newUserInLocalDb = await dbLocal.Users.FirstAsync(_ => _.Name == newUser.Name);
                    newUserInLocalDb.Name = "modified-name";
                    await dbLocal.SaveChangesAsync();

                    //get changes from local db
                    var localChangeSet = await localSyncProvider.GetIncreamentalChangesAsync(finalAnchor);
                    Assert.IsNotNull(localChangeSet);
                    Assert.AreEqual(2, ((SqlSyncAnchor)localChangeSet.Anchor).Version);

                    //try to apply changes to remote provider
                    var anchorAfterChangesAppliedFromLocalProvider =
                        await remoteSyncProvider.ApplyChangesAsync(new SqlSyncChangeSet((SqlSyncAnchor)changeSetAfterUserAdd.Anchor, localChangeSet.Items));
                    //given we didn't provide a resolution function for the conflict provider just skip 
                    //to apply the changes from local db
                    //so nothing should be changed in remote db
                    Assert.IsNotNull(anchorAfterChangesAppliedFromLocalProvider);
                    Assert.AreEqual(2, ((SqlSyncAnchor)anchorAfterChangesAppliedFromLocalProvider).Version);

                    var userNotChangedInRemoteDb = await dbRemote.Users.FirstAsync(_ => _.Email == newUser.Email);
                    Assert.IsNotNull(userNotChangedInRemoteDb);
                    Assert.AreEqual(newUser.Name, userNotChangedInRemoteDb.Name);

                    //ok now try apply changes but forcing any write on remote store on conflict
                    anchorAfterChangesAppliedFromLocalProvider =
                        await remoteSyncProvider.ApplyChangesAsync(new SqlSyncChangeSet((SqlSyncAnchor)changeSetAfterUserAdd.Anchor, localChangeSet.Items), 
                        (item) =>
                        {
                            //assert that conflict occurred on item we just got from local db
                            Assert.IsNotNull(item);
                            Assert.AreEqual(newUserInLocalDb.Email, item.Values["Email"]);
                            Assert.AreEqual(newUserInLocalDb.Name, item.Values["Name"]);
                            Assert.AreEqual(ChangeType.Update, item.ChangeType);

                            //force write in remote store
                            return ConflictResolution.ForceWrite;
                        });

                    //now we should have a new version  (+1)
                    Assert.IsNotNull(anchorAfterChangesAppliedFromLocalProvider);
                    Assert.AreEqual(3, ((SqlSyncAnchor)anchorAfterChangesAppliedFromLocalProvider).Version);

                    //and local db changes should be applied to remote db
                    var userChangedInRemoteDb = await dbRemote.Users.AsNoTracking().FirstAsync(_ => _.Email == newUser.Email);
                    Assert.IsNotNull(userChangedInRemoteDb);
                    Assert.AreEqual(newUserInLocalDb.Name, userChangedInRemoteDb.Name);
                }

                {
                    //now let's try to update a deleted record
                    dbRemote.Users.Remove(newUser);
                    await dbRemote.SaveChangesAsync();

                    var newUserInLocalDb = await dbLocal.Users.FirstAsync(_ => _.Email == newUser.Email);
                    var localChangeSet = await localSyncProvider.GetIncreamentalChangesAsync(finalAnchor);
                    Assert.IsNotNull(localChangeSet);
                    Assert.AreEqual(2, ((SqlSyncAnchor)localChangeSet.Anchor).Version);

                    //try to apply changes to remote provider
                    var anchorAfterChangesAppliedFromLocalProvider =
                        await remoteSyncProvider.ApplyChangesAsync(new SqlSyncChangeSet((SqlSyncAnchor)changeSetAfterUserAdd.Anchor, localChangeSet.Items));
                    //given we didn't provide a resolution function for the conflict provider just skip 
                    //to apply the changes from local db
                    //so nothing should be changed in remote db
                    Assert.IsNotNull(anchorAfterChangesAppliedFromLocalProvider);
                    Assert.AreEqual(4, ((SqlSyncAnchor)anchorAfterChangesAppliedFromLocalProvider).Version);

                    //user should not be present
                    var userNotChangedInRemoteDb = await dbRemote.Users.FirstOrDefaultAsync(_ => _.Email == newUser.Email);
                    Assert.IsNull(userNotChangedInRemoteDb);

                    //ok now try apply changes but forcing any write on remote store on conflict
                    anchorAfterChangesAppliedFromLocalProvider =
                        await remoteSyncProvider.ApplyChangesAsync(new SqlSyncChangeSet((SqlSyncAnchor)changeSetAfterUserAdd.Anchor, localChangeSet.Items),
                        (item) =>
                        {
                            //assert that conflict occurred on item we just got from local db
                            Assert.IsNotNull(item);
                            Assert.AreEqual(newUserInLocalDb.Email, item.Values["Email"]);
                            Assert.AreEqual(newUserInLocalDb.Name, item.Values["Name"]);
                            Assert.AreEqual(ChangeType.Update, item.ChangeType);

                            //force write in remote store
                            return ConflictResolution.ForceWrite;
                        });

                    //now we should have a new version  (+1)
                    Assert.IsNotNull(anchorAfterChangesAppliedFromLocalProvider);
                    Assert.AreEqual(5, ((SqlSyncAnchor)anchorAfterChangesAppliedFromLocalProvider).Version);

                    //and local db changes should be applied to remote db
                    var userChangedInRemoteDb = await dbRemote.Users.AsNoTracking().FirstAsync(_ => _.Email == newUser.Email);
                    Assert.IsNotNull(userChangedInRemoteDb);
                    Assert.AreEqual(newUserInLocalDb.Name, userChangedInRemoteDb.Name);

                }
            }


        }
    }
}
