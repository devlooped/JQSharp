map(tojson | fromjson | .address.zip) | unique | length
