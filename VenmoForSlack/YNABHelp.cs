namespace VenmoForSlack
{
    public static class YNABHelp
    {
        public const string HelpMessage =
@"YNAB help
For an online version of this go to https://venmo.golf1052.com/
Commands:
venmo ynab list account(s)
    lists your accounts under your default budget
venmo ynab set account {number}
    sets the default account to use. you must set a default account
venmo ynab list categories
    lists your YNAB categories
venmo ynab list mapping
    list all Venmo to YNAB mappings
venmo ynab set mapping ""{note}"" to ""{YNAB category}""
    create or update a mapping from a Venmo note to a YNAB category
venmo ynab delete mapping {number}
    delete a mapping
venmo ynab delete auth
    Deletes your YNAB authentication information from the database but retains your settings (mappings).
venmo ynab delete everything
    Deletes all of your information including your YNAB authentication information and settings (mappings) from the database.
    This is not reversible.
venmo ynab code code
    code = YNAB authentication code
venmo ynab help
    this help method";
    }
}
