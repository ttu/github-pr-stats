using JsonFlatFileDataStore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GitHubStats
{
    public static class DataStoreExtensions
    {
        public static Task SaveBatch(this IDataStore dataStore, IEnumerable<IUserPrData> datas)
        {
            return Task.Run(async () =>
            {
                var collection = dataStore.GetCollection<User>();

                var toSave = datas.Where(e => e != null).Select(user =>
                {
                    var fromDb = collection.Find(e => e.GitHubId == user.GitHubId).First();
                    fromDb.Last_Update = user.Last_Update;
                    fromDb.PR_Count = user.PR_Count;

                    if (fromDb.PR_Count != fromDb.Items.Count)
                    {
                        var newItems = user.Items.Where(i => !fromDb.Items.Any(e => e.Id == i.Id));
                        fromDb.Items.AddRange(newItems);
                    }

                    return fromDb;
                });

                var tasks = toSave.Select(fromDb => collection.ReplaceOneAsync(e => e.Id == fromDb.Id, fromDb));
                await Task.WhenAll(tasks);
            });
        }
    }
}