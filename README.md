# BOINC Dashboard

<img width="1484" height="867" alt="image" src="https://github.com/user-attachments/assets/c08aabda-7071-4b1c-a174-8f301c472e77" />

Looking for a sleek, modern way to see task status across multiple BOINC clients on your LAN? Look no further.

- Tasks tab: see all tasks across all statuses across all computers
- Computers tab: see all computers (online or offline)

# Usage

Add `hosts.json` following this template:

```json
[
    { 
        "Name": "machine name 1", 
        "Address": "127.0.0.1", 
        "Password": "password from gui_rpc_auth.cfg" 
    },
    { 
        "Name": "second machine", 
        "Address": "192.168.1.123", 
        "Password": "password from gui_rpc_auth.cfg on that machine" 
    }
]
```

Run the app and you're flying.
