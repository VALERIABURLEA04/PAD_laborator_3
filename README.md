# BookCatalog

API REST pentru cărți, cu Controllers, OpenAPI, MongoDB, Redis, Service Discovery și un Smart Proxy în spatele Nginx. Clientul poate trimite și primi JSON sau XML. Căile HTTP reprezintă entități (fără verbe), iar API-ul oferă întregul CRUD.

## Arhitectura

```
Client (Postman / browser)
   │
   ▼
Nginx :8080  ──►  Smart Proxy (gateway) ──► api1 ──► rs1 (mongo1a, mongo1b, mongo1arb)
                     │   │   │          ──► api2 ──► rs2 (mongo2a, mongo2b, mongo2arb)
                     │   │   │          ──► api3 ──► rs3 (mongo3a, mongo3b, mongo3arb)
                     │   │   └── Redis (cache de răspunsuri, TTL)
                     │   └────── Discovery ◄── API-urile se înregistrează + heartbeat
                     └────────── Docker socket (Kill / Restart din dashboard)

sync ── citește cele 3 replica set-uri și propagă schimbările între baze
```

| Componentă | Rol |
| --- | --- |
| `nginx` | Punct de intrare pe portul 8080. Trimite totul către Smart Proxy. |
| `gateway` (`BookCatalog.Gateway`) | Smart Proxy: round robin / sticky, health check activ, circuit breaker, coadă cu priorități, cache Redis, retry, metrici, dashboard. |
| `discovery` (`BookCatalog.Discovery`) | Registrul de servicii. Fiecare API se anunță la pornire (`POST /services`), trimite heartbeat la 5 s și se retrage la oprire. |
| `api1`, `api2`, `api3` | Instanțele API, fiecare cu baza ei MongoDB (replica set Primary + Secondary + Arbiter). |
| `sync` | Sincronizează cele trei baze independente (versiuni + marcaje de ștergere). |
| `redis` | Cache-ul gateway-ului (și cache-ul intern al API-urilor pentru GET după id). |

### Fluxul unei cereri

1. Clientul trimite cererea la Nginx, care o dă Smart Proxy-ului.
2. **Cache:** pentru `GET /api/books…`, gateway-ul caută întâi în Redis. La HIT răspunde imediat (`X-Cache: HIT`, `X-Cache-TTL`), fără să ajungă la API.
3. **Coada cu priorități:** cel mult `MaxConcurrency` (implicit 4) cereri merg simultan la API-uri. Restul așteaptă în cozile HIGH / NORMAL / LOW (header `X-Priority`). Un loc liber se dă mereu cozii cu prioritatea cea mai mare.
4. **Load balancing:** se aleg doar instanțele care sunt UP în Discovery (heartbeat), UP la health check și au circuitul închis. Implicit **round robin**. Cu header-ul `X-Client-Id`, sau cu modul global **Sticky**, clientul ajunge mereu pe același nod (rendezvous hashing).
5. **Circuit breaker:** după 3 eșecuri consecutive (eroare de conexiune, timeout sau 5xx) circuitul instanței se deschide pentru 15 s. Apoi trece în HALF-OPEN și lasă o singură cerere de probă.
6. **Retry:** dacă o instanță cade, cererile idempotente (GET, PUT, DELETE) se reîncearcă pe următoarea. POST se reîncearcă doar dacă conexiunea nu s-a putut deschide.
7. La POST / PUT / DELETE reușite, cache-ul pentru `/api/books*` se invalidează imediat și încă o dată după ~3 s, după propagarea prin `sync`.

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

Seed-ul adaugă 300 de cărți demonstrative în prima bază (`SEED_COUNT` în `compose.yaml`). Serviciul `sync` le copiază în celelalte două. Scriptul poate fi rulat din nou fără dubluri.

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
7. **Failover Mongo:** **Kill** pe `mongo1a`. `mongo1b` devine Primary, iar api1 continuă să funcționeze.

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

| API | Baza de date | Primary inițial | Secondary | Arbiter | Replica set |
| --- | --- | --- | --- | --- | --- |
| api1 | bookcatalog1 | mongo1a | mongo1b | mongo1arb | rs1 |
| api2 | bookcatalog2 | mongo2a | mongo2b | mongo2arb | rs2 |
| api3 | bookcatalog3 | mongo3a | mongo3b | mongo3arb | rs3 |

Fiecare Secondary păstrează o copie a bazei din perechea sa. Arbiterul votează la alegerea unui nou Primary. Serviciul `sync` propagă schimbările între bazele independente, aproximativ o dată pe secundă, așa că toate trei ajung la același set de cărți. Aceasta este replicare și sincronizare la nivelul aplicației, nu sharding MongoDB nativ (`mongos` + config servers).

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

`test-smart-proxy.ps1` verifică discovery, cache-ul (MISS → HIT), XML, round robin, sticky și coada cu priorități. Celelalte scripturi verifică CRUD-ul, sincronizarea între baze și cererile concurente.

Pentru failover din linia de comandă: `docker compose stop mongo1a`, așteaptă alegerea noului Primary, rulează `.\scripts\test-cluster.ps1`, apoi `docker compose start mongo1a`.

Pentru eliberarea cache-ului vechi de build Docker: `.\scripts\clear-docker-cache.ps1`. Scriptul păstrează volumele cu date.
