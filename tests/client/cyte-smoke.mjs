/* Live check against the running web app's model endpoint, and through it the
   real Cyte gateway. Start the server first, then:

     node tests/client/cyte-smoke.mjs [baseUrl]

   Exits non-zero if the gateway is not reachable or returns no models. */
const base = process.argv[2] || "http://127.0.0.1:5000";

let failures = 0;
const check = (name, cond, detail) => {
  if (cond) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); failures++; }
};

let res, text, body;
try {
  res = await fetch(`${base}/api/models`);
  text = await res.text();
  try { body = JSON.parse(text); } catch (_) { }
} catch (err) {
  console.error(`ERROR: could not reach ${base}/api/models`);
  console.error(`  ${err.message}`);
  console.error(`  Is the web app running? Start it with: cd src/HazardRecon.Web && dotnet run`);
  process.exit(1);
}

console.log(`GET ${base}/api/models -> ${res.status}`);
check("responded 200", res.status === 200, text.slice(0, 200));
check("returned an array", Array.isArray(body), typeof body);

if (Array.isArray(body)) {
  check("at least one model", body.length > 0, `got ${body.length}`);
  for (const m of body) {
    check(`model has id + friendlyName (${m.friendlyName || "?"})`,
      Boolean(m.id) && Boolean(m.friendlyName), JSON.stringify(m));
  }
  console.log("\nmodels:");
  for (const m of body) console.log(`  ${m.id}  provider=${m.provider}  ${m.friendlyName}  (${m.modelName})`);
}

console.log(failures === 0 ? "\nSMOKE PASSED" : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
