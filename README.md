# BookCatalog

API REST pentru cărți, cu Controllers, OpenAPI, MongoDB, Redis, Service Discovery și un Smart Proxy în spatele Nginx. Clientul poate trimite și primi JSON sau XML. Căile HTTP reprezintă entități (fără verbe), iar API-ul oferă întregul CRUD.

## Arhitectura

```
Client (Postman / browser)
   │
   ▼
Nginx :8080  ──►  Smart Proxy (gateway) ──► api1 ─┐      ┌─► shard1 = rs1 (mongo1a, mongo1b, mongo1arb)
                     │   │   │          ──► api2 ─┼─ hash(id) ─► shard2 = rs2 (mongo2a, mongo2b, mongo2arb)
                     │   │   │          ──► api3 ─┘      └─► shard3 = rs3 (mongo3a, mongo3b, mongo3arb)
                     │   │   └── Redis (cache de răspunsuri, TTL)
                     │   └────── Discovery ◄── API-urile se înregistrează + heartbeat
                     └────────── Docker socket (Kill / Restart din dashboard)
```

| Componentă | Rol |
| --- | --- |
| `nginx` | Punct de intrare pe portul 8080. Trimite totul către Smart Proxy. |
| `gateway` (`BookCatalog.Gateway`) | Smart Proxy: round robin / sticky, health check activ, circuit breaker, coadă cu priorități, cache Redis, retry, metrici, dashboard. |
| `discovery` (`BookCatalog.Discovery`) | Registrul de servicii. Fiecare API se anunță la pornire (`POST /services`), trimite heartbeat la 5 s și se retrage la oprire. |
| `api1`, `api2`, `api3` | Instanțele API. Fiecare cunoaște toate cele 3 shard-uri și trimite fiecare carte în shard-ul care o deține. |
| `mongo1*`, `mongo2*`, `mongo3*` | Trei shard-uri; fiecare shard este un replica set Primary + Secondary + Arbiter. |
| `redis` | Cache-ul gateway-ului (și cache-ul intern al API-urilor pentru GET după id). |

### Fluxul unei cereri

1. Clientul trimite cererea la Nginx, care o dă Smart Proxy-ului.
2. **Cache:** pentru `GET /api/books…`, gateway-ul caută întâi în Redis. La HIT răspunde imediat (`X-Cache: HIT`, `X-Cache-TTL`), fără să ajungă la API.
3. **Coada cu priorități:** cel mult `MaxConcurrency` (implicit 4) cereri merg simultan la API-uri. Restul așteaptă în cozile HIGH / NORMAL / LOW (header `X-Priority`). Un loc liber se dă mereu cozii cu prioritatea cea mai mare.
4. **Load balancing:** se aleg doar instanțele care sunt UP în Discovery (heartbeat), UP la health check și au circuitul închis. Implicit **round robin**. Cu header-ul `X-Client-Id`, sau cu modul global **Sticky**, clientul ajunge mereu pe același nod (rendezvous hashing).
5. **Circuit breaker:** după 3 eșecuri consecutive (eroare de conexiune, timeout sau 5xx) circuitul instanței se deschide pentru 15 s. Apoi trece în HALF-OPEN și lasă o singură cerere de probă.
6. **Retry:** dacă o instanță cade, cererile idempotente (GET, PUT, DELETE) se reîncearcă pe următoarea. POST se reîncearcă doar dacă conexiunea nu s-a putut deschide.
7. **Sharding:** API-ul calculează `hash(id)` și scrie sau citește cartea doar din shard-ul care o deține. `GET /api/books` întreabă toate shard-urile în paralel și combină rezultatele.
8. La POST / PUT / DELETE reușite, cache-ul pentru `/api/books*` se invalidează.

### Sharding

Datele sunt împărțite între cele trei baze, nu copiate în toate. Cheia de sharding este id-ul cărții:

1. Id-ul (ObjectId) trece printr-un hash pe 32 de biți (FNV-1a urmat de pasul final de amestecare din MurmurHash3), deci rezultatul e un număr între `0x00000000` și `0xFFFFFFFF`.
2. Spațiul acesta e împărțit în trei intervale egale: `shard1` ia prima treime, `shard2` a doua, `shard3` a treia.
3. Cartea stă doar în shard-ul al cărui interval conține hash-ul ei.

Hash-ul e necesar pentru că ObjectId începe cu momentul creării: un interval direct pe id ar trimite toate cărțile noi în ultimul shard. Este aceeași idee ca *hashed sharding* din MongoDB, implementată în aplicație (`BookCatalog.Api/Services/ShardRouter.cs`).

- Scrierile se împart pe trei baze, deci crește viteza de scriere. Fiecare bază are doar o treime din date, deci citirile sunt mai rapide.
- **Replicarea rămâne în fiecare shard:** dacă Primary-ul unui shard cade, Secondary-ul preia rolul, iar cărțile shard-ului rămân disponibile.
- Orice API poate servi orice carte, așa că round robin, sticky, circuit breaker și retry funcționează la fel.
- `GET /api/shards` arată pentru fiecare shard intervalul, numărul de cărți și nodul Primary. Dashboard-ul le afișează în „Rafturile bibliotecii”.
- Header-ul `X-Shard` din răspuns arată în ce shard s-a scris sau s-a citit cartea.
- **Shard căzut complet** (ambele noduri cu date oprite): cărțile din celelalte shard-uri funcționează normal. `GET /api/books` întoarce ce poate, cu header-ul `X-Shards-Unavailable`. O carte din shard-ul căzut primește `503` cu `X-Shard-Unavailable`. Gateway-ul nu numără acest răspuns ca defect al API-ului (nu deschide circuitul și nu face retry), pentru că problema e în stratul de date, nu în instanță.

### Service Discovery, heartbeat și health check

- **Push (heartbeat):** API-ul apelează `POST http://discovery:8080/services` la pornire, apoi `PUT /services/{id}/heartbeat` la fiecare 5 s, iar la oprire `DELETE /services/{id}`. Fără heartbeat 15 s, instanța devine `EXPIRED`.
- **Pull:** gateway-ul citește lista din Discovery la fiecare 2 s, deci nu are API-urile scrise fix în configurare.
- **Health check activ:** gateway-ul apelează `/health` pe fiecare instanță la 3 s. `/health` verifică MongoDB-ul instanței și Redis.

## Pornire și oprire

Din folderul proiectului:

```powershell
docker compose up --build -d --remove-orphans
docker compose --profile seed run --rm seed
```

Seed-ul adaugă 300 de cărți demonstrative (`SEED_COUNT` în `compose.yaml`), fiecare în shard-ul care o deține, deci ~100 în fiecare bază. Scriptul poate fi rulat din nou fără dubluri. Dacă bazele au date dintr-o versiune mai veche (când toate bazele aveau toate cărțile), seed-ul mută fiecare carte în shard-ul corect și șterge copiile în plus.

| Adresă | Ce este |
| --- | --- |
| http://localhost:8080/dashboard/ | **Dashboard-ul Smart Proxy** |
| http://localhost:8080/api/books | API-ul prin Nginx + gateway |
| http://localhost:8080/openapi/v1.json | OpenAPI (prin gateway, de la una dintre instanțe) |
| http://localhost:18080 | Gateway-ul direct, fără Nginx |
| http://localhost:18090/services | Registrul Discovery |
| http://localhost:18081, 18082, 18083 | API-urile direct |

Oprire: `docker compose down`. Nu adăuga `-v`: volumele păstrează datele MongoDB.

## Dashboard

Dashboard-ul **„BookCatalog — Centrul de control al bibliotecii”** (`http://localhost:8080/dashboard/`) se actualizează la fiecare secundă și arată:

- **Backend-uri:** health check, heartbeat (cu vechimea ultimului), starea circuitului (CLOSED / OPEN / HALF-OPEN), cereri și eșecuri, latență. Butoane **Kill** (oprește brusc containerul), **Restart** și **Reset circuit**.
- **Latențe** P50 / P95 / P99, cereri pe secundă, erori și retry-uri.
- **Cache Redis:** hit ratio, hits, misses, chei, TTL. Buton pentru golirea cache-ului.
- **Priority queue:** câte cereri așteaptă acum pe HIGH / NORMAL / LOW, câte au fost procesate și timpul mediu de așteptare.
- **Test cerere:** orice metodă și cale, cu format JSON sau XML, prioritate, `X-Client-Id` și no-cache. Afișează header-ele `X-Cache`, `X-Api-Instance`, `X-Gateway-Backend` etc.
- **Test de încărcare:** trimite N cereri simultan cu priorități amestecate. LOW pleacă primele, HIGH ultimele, iar tabelul arată că HIGH așteaptă totuși cel mai puțin.
- **Containere:** starea tuturor serviciilor, cu Kill / Start pentru failover (de exemplu `mongo1a`, `redis`, `discovery`). `gateway` și `nginx` sunt protejate.

Pentru butoanele Kill / Restart, gateway-ul are montat `/var/run/docker.sock` și rulează ca root. Este acceptabil pentru un laborator local, dar nu pentru producție.

### Scenarii pentru prezentare

1. **Round robin:** trimite de câteva ori `GET /api/books/{id}` cu no-cache. `X-Api-Instance` se schimbă api1 → api2 → api3.
2. **Sticky:** pune un `X-Client-Id` sau comută butonul **Sticky**. Toate cererile ajung pe același nod.
3. **Cache:** același GET de două ori dă `MISS`, apoi `HIT`. După TTL (30 s) sau după un PUT redevine `MISS`.
4. **Heartbeat + health check:** **Kill** pe api2. Health check-ul îl marchează DOWN în ~3 s, heartbeat-ul îl face EXPIRED în ~15 s, iar traficul merge doar la api1 și api3. **Restart**: api2 se reînregistrează singur și primește din nou cereri.
5. **Circuit breaker:** imediat după Kill, rulează un test de încărcare. Cererile către api2 eșuează, se reîncearcă pe alt nod, iar circuitul lui api2 devine OPEN. După 15 s trece în HALF-OPEN.
6. **JSON ↔ XML:** în „Test cerere” comută formatul. Același endpoint întoarce XML, iar POST acceptă corp XML.
7. **Sharding:** în „Rafturile bibliotecii” cele ~300 de cărți sunt împărțite cam 100 / 100 / 100. Creează o carte din „Test cerere”: header-ul `X-Shard` arată în ce shard a ajuns, iar contorul acelui shard crește cu 1.
8. **Failover Mongo:** **Kill** pe `mongo1a`. `mongo1b` devine Primary, iar api1 continuă să funcționeze.

## Postman

Importă `postman/BookCatalog.postman_collection.json`. Folderele merg în ordine: CRUD JSON, XML, Cache, Load balancing, Priority queue, Discovery / health / circuit breaker. Variabila `bookId` se completează automat după POST.

## Endpointuri

### API (prin gateway)

| Metodă | Cale | Rezultat |
| --- | --- | --- |
| GET | `/api/books` | Lista cărților active |
| GET | `/api/books/{id}` | O carte sau `404` |
| POST | `/api/books` | Creează o carte și întoarce `201` |
| PUT | `/api/books/{id}` | Înlocuiește o carte sau întoarce `404` |
| DELETE | `/api/books/{id}` | Șterge o carte și întoarce `204` |

Corpul pentru POST și PUT conține `title`, `author`, `publishedYear`, opțional `description` și `tags`. Formatul se alege cu `Accept` / `Content-Type` (`application/json` sau `application/xml`) sau cu `?format=xml`. În XML, elementul rădăcină este `<Book>`, iar etichetele sunt `<Tags><Tag>…</Tag></Tags>`.

Header-e opționale: `X-Priority: high|normal|low`, `X-Client-Id: <orice>` (sticky), `Cache-Control: no-cache` (ocolește cache-ul).

### Gateway

| Metodă | Cale | Rezultat |
| --- | --- | --- |
| GET | `/gateway/stats` | Tot ce afișează dashboard-ul |
| GET | `/gateway/backends` | Instanțele cunoscute și starea lor |
| PUT | `/gateway/backends/{id}/circuit` | Închide circuitul unei instanțe |
| PUT | `/gateway/balancing` | `{"mode":"RoundRobin"}` sau `{"mode":"Sticky"}` |
| DELETE | `/gateway/cache` | Golește cache-ul gateway-ului |
| DELETE | `/gateway/statistics` | Resetează metricile |
| GET | `/gateway/containers` | Containerele din proiectul Compose |
| PUT | `/gateway/containers/{service}/state` | `{"state":"stopped"}` (kill) sau `{"state":"running"}` |
| POST | `/gateway/load-tests` | `{"total":120,"highPercent":20,"lowPercent":50,"bypassCache":true}` |

### Discovery

| Metodă | Cale | Rezultat |
| --- | --- | --- |
| GET | `/services?name=bookcatalog-api` | Instanțele înregistrate (`up`, `expired`, `down`) |
| POST | `/services` | Înregistrare `{id, name, address}` |
| PUT | `/services/{id}/heartbeat` | Heartbeat |
| DELETE | `/services/{id}` | Retragere |

## Topologia MongoDB

| Shard | Baza de date | Primary inițial | Secondary | Arbiter | Replica set |
| --- | --- | --- | --- | --- | --- |
| shard1 | bookcatalog1 | mongo1a | mongo1b | mongo1arb | rs1 |
| shard2 | bookcatalog2 | mongo2a | mongo2b | mongo2arb | rs2 |
| shard3 | bookcatalog3 | mongo3a | mongo3b | mongo3arb | rs3 |

Fiecare shard este un replica set: Secondary-ul păstrează o copie a datelor shard-ului (replicare automată MongoDB), iar arbiterul votează la alegerea unui nou Primary. Shard-urile nu își copiază datele între ele: fiecare carte există într-un singur shard.

Fiecare API folosește `w=1`, ca scrierile să poată continua după căderea Primary-ului. O scriere confirmată cu `w=1` se poate pierde dacă noul Primary cade înainte să fie replicată.

## Resurse

Toate serviciile au limite de memorie în `compose.yaml`. Consumul real se vede cu `docker stats --no-stream`. Redis folosește cel mult 32 MB pentru date, fără persistență.

## Testare

```powershell
.\scripts\test-cluster.ps1
.\scripts\test-crud.ps1
.\scripts\test-gateway.ps1
.\scripts\test-concurrency.ps1
.\scripts\test-smart-proxy.ps1
```

`test-smart-proxy.ps1` verifică discovery, cache-ul (MISS → HIT), XML, round robin, sticky și coada cu priorități. De asemenea verifică sharding-ul: fiecare shard are cărți, iar suma lor e egală cu totalul. Celelalte scripturi verifică CRUD-ul, faptul că orice API vede aceeași carte și cererile concurente.

Pentru failover din linia de comandă: `docker compose stop mongo1a`, așteaptă alegerea noului Primary, rulează `.\scripts\test-cluster.ps1`, apoi `docker compose start mongo1a`.

Pentru eliberarea cache-ului vechi de build Docker: `.\scripts\clear-docker-cache.ps1`. Scriptul păstrează volumele cu date.
