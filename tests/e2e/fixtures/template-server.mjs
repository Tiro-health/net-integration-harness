// A stand-in for the template server, holding exactly one Questionnaire.
//
// The floor cell runs the harness against an SDC server container pinned to
// SdcCompatibility.MinimumSdcVersion. That container is empty: the SDC server does not store
// questionnaires, it resolves them over HTTP. The chain, verified against the shipped SDK
// bundle and the server source rather than guessed:
//
//   element  ->  GET {sdcEndpoint}/Questionnaire/$resolve?canonical=<canonical>
//   sdc      ->  GET {TEMPLATE_SERVER_URL}/fhir/r5/Questionnaire/$resolve?canonical=<canonical>
//            ->  a single Questionnaire resource (not a searchset Bundle)
//
// So this serves that one route, and the container is started with TEMPLATE_SERVER_URL
// pointing here. Nothing in the pull-request path then touches a shared server — which is the
// entire reason the floor cell exists, and why pointing the container at staging instead would
// have given up most of the point.
//
// The fixture is the real published revision, exported verbatim from that same $resolve route
// on staging. Hand-writing one would have meant rewriting the assertions that depend on its
// content: the chip label, the disjoint linkIds that make "which revision rendered" observable,
// and the calculatedExpression the score comes from.
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
const FIXTURE_PATH = join(HERE, "questionnaire-cha2ds2-vasc-1.0.0.json");
const fixture = JSON.parse(readFileSync(FIXTURE_PATH, "utf8"));

// What this server will answer to, built from the fixture rather than repeated as a literal —
// a canonical that disagreed with the resource it returns is the exact confusion this suite
// exists to catch.
const CANONICAL = `${fixture.url}|${fixture.version}`;
const PORT = Number(process.env.TEMPLATE_SERVER_PORT ?? 8089);

const outcome = (severity, code, text) => JSON.stringify({
  resourceType: "OperationOutcome",
  issue: [{ severity, code, details: { text } }],
});

const server = createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const canonical = url.searchParams.get("canonical");

  // Logged unconditionally: when the floor cell fails, the first question is always "what did
  // the server actually ask for", and a silent stub makes that unanswerable.
  console.log(`[template-server] ${req.method} ${url.pathname} canonical=${canonical ?? "(none)"}`);

  if (url.pathname !== "/fhir/r5/Questionnaire/$resolve") {
    // Deliberately not a catch-all that returns the fixture anyway. A stub that answers
    // everything would let a change in which route the server calls pass unnoticed, and the
    // suite would go on testing a path the product no longer uses.
    res.writeHead(404, { "Content-Type": "application/fhir+json" });
    res.end(outcome("error", "not-found", `template-server stub serves only /fhir/r5/Questionnaire/$resolve, got ${url.pathname}`));
    return;
  }

  if (canonical !== CANONICAL) {
    res.writeHead(404, { "Content-Type": "application/fhir+json" });
    res.end(outcome("error", "not-found", `no questionnaire for canonical '${canonical}'; this stub holds only '${CANONICAL}'`));
    return;
  }

  res.writeHead(200, { "Content-Type": "application/fhir+json; fhirVersion=5.0" });
  res.end(JSON.stringify(fixture));
});

server.listen(PORT, "0.0.0.0", () => {
  console.log(`[template-server] listening on 0.0.0.0:${PORT}, holding ${CANONICAL}`);
});
