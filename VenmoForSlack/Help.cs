namespace VenmoForSlack
{
    public static class Help
    {
        public const string HelpMessage =
@"Venmo help
Commands:
venmo balance
    returns your Venmo balance
venmo last
    returns your last command
venmo (audience) pay/charge amount for note to recipients
    example: venmo public charge $10.00 for lunch to testuser phone:5555555555 email:example@example.com
    supports basic arithmetic, does not follow order of operations or support parenthesis
    example: venmo charge 20 + 40 / 3 for brunch to a_user boss phone:5556667777
        this would charge $20 NOT $33.33 to each user in the recipients list
    audience (optional) = public OR friends OR private
        defaults to private if omitted
    pay/charge = pay OR charge
    amount = Venmo amount
    note = Venmo message
    recipients = list of recipients, can specify Venmo username, phone number prefixed with phone: or email prefixed with email:
venmo alias id alias
    example: venmo alias 4u$3r1d sam
    set an alias for a Venmo username
    id = Venmo username
    alias = the alias for that user, must not contain spaces
venmo alias list
    list all aliases
venmo alias delete alias
    example: venmo alias delete sam
    delete an alias
    alias = the alias for that user, must not contain spaces
venmo pending (to OR from)
    returns pending venmo charges, defaults to to
    also returns ID for payment completion
venmo complete accept/reject/cancel number(s)/all
    accept OR reject pending incoming Venmos with the given IDs
    cancel pending outgoing Venmos with the given IDs
venmo code code
    code = Venmo authentication code
venmo help
    this help message";
    }
}
