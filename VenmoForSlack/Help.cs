namespace VenmoForSlack
{
    public static class Help
    {
        public const string HelpMessage =
@"Venmo help
For an online version of this go to https://venmo.golf1052.com/
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
venmo schedule {recurrence} {execution date} {payment command}
    example: venmo schedule every beginning of the month pay $10 for Netflix to testuser
        this would pay $10 every 1st of the month to testuser
    note that scheduled Venmos will always execute at 12 PM on the scheduled day unless otherwise specified and will never execute on the current day, so for example if today is Wednesday and you schedule a Venmo for Wednesday it will be scheduled for next week's Wednesday
    recurrence: either 'every' meaning repeated or 'on' OR 'at' meaning one-time
    execution date: supports
        day or tomorrow: the next day
        sunday
        monday
        tuesday
        wednesday
        thursday
        friday
        saturday
        beginning of the month: the first of the month
        end of the month: the last day of the month
        day of the month (1/2/3/.../29/30/31): specified day of the month, if a month doesn't have the specified day of the month the scheduled Venmo will be executed on the last day of the month
        ISO 8601 string: examples: 2020-02-29 or 2020-02-29T18:30. https://en.wikipedia.org/wiki/ISO_8601
    payment command: a valid payment command
venmo schedule list
    list all scheduled Venmos
venmo schedule delete ###
    delete the specified scheduled Venmo
venmo code code
    code = Venmo authentication code
venmo help
    this help message";
    }
}
