# Contributing

## Testing

- Install [Microsoft Dev Tunnels](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/) or [ngrok](https://ngrok.com/).
- `devtunnel host -p 8900 --allow-anonymous` or `ngrok http 8900`
- To develop locally you'll need to edit three Slack urls
  - Under "Interactivity & Shortcuts" set "Request URL"
    - Set back to "https://venmo.golf1052.com/interactive"
  - Under "Slash Commands" edit "/venmo" "Request URL"
    - Set back to "https://venmo.golf1052.com"
  - Under "Event Subscriptions" set "Request URL"
    - Set back to "https://venmo.golf1052.com/events"
