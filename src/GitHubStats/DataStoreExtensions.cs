using JsonFlatFileDataStore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GitHubStats
{
    public static class DataStoreExtensions
    {
        public static async Task SaveBatch(this IDataStore dataStore, IEnumerable<IUserPrData> datas)
        {
            var tasks = datas.Where(e => e != null)
                             .Select(user =>
                             {
                                 // Wrap save to a Task as handling large collections gets slow
                                 return Task.Run(async () =>
                                 {
                                     var fromDb = dataStore.GetCollection<User>().AsQueryable().First(e => e.GitHubId == user.GitHubId);
                                     fromDb.Last_Update = user.Last_Update;
                                     fromDb.PR_Count = user.PR_Count;

                                     if (fromDb.PR_Count != fromDb.Items.Count)
                                     {
                                         var newItems = user.Items.Where(i => !fromDb.Items.Any(e => e.Id == i.Id));
                                         fromDb.Items.AddRange(newItems);
                                     }

                                     return await dataStore.GetCollection<User>().ReplaceOneAsync(e => e.Id == fromDb.Id, fromDb);
                                 });
                             });

            await Task.WhenAll(tasks);
        }
    }
}