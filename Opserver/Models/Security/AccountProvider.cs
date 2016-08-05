namespace StackExchange.Opserver.Models.Security
{
    /// <summary>
    /// Does this REALLY need an explanation?
    /// </summary>
    public class AccountProvider : SecurityProvider
    {
        public override bool IsAdmin => Current.User.AccountName == "admin";

        internal override bool InReadGroups(ISecurableSection settings) { return true; }
        public override bool InGroups(string groupNames, string accountName) { return true; }
        public override bool ValidateUser(string userName, string password)
        {
            foreach (var a in SecuritySettings.Current.Accounts.All)
            {
                if (a.Name == userName && a.Password == password)
                {
                    return true;
                }
            }
            return false;
        }
    }
}