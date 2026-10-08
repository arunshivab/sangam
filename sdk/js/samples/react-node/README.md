# Sample: React + Node

Signs in with the development Sangam (http://localhost:5100) as its sample application, on http://localhost:5910.

```sh
cd sdk/js && npm ci && npm run build
npm start -w sangam-sample-react-node
```

It shows the person's organisations, checks a permission in the browser and on the server, and signs a record: that
needs a two-factor sign-in from the last five minutes, so Sangam asks again first, and the sample writes a shared audit
event to `sangam-audit/pending.jsonl`. Settings: `SANGAM_AUTHORITY`, `SANGAM_CLIENT_ID`, `SANGAM_CLIENT_SECRET`,
`BASE_URL`, `COOKIE_SECRET`, `AUDIT_BUFFER`. `sdk/conformance/live/check_samples.py` drives it end to end.
