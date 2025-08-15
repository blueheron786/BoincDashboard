# BOINC Dashboard

Looking for a sleek, modern way to see task status across multiple BOINC clients on your LAN? Look no further.

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