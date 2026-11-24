# BookCatalog

API REST pentru cărți, cu Controllers, OpenAPI, MongoDB, Redis și Nginx. Clientul trimite și primește JSON. Căile HTTP reprezintă entități, iar API-ul oferă întregul CRUD.

## Topologia

Fișierul unic `compose.yaml` pornește trei API-uri și trei replica set-uri MongoDB independente:

| API | Baza de date | Primary inițial | Secondary | Arbiter | Replica set |
| --- | --- | --- | --- | --- | --- |
| api1 | bookcatalog1 | mongo1a | mongo1b | mongo1arb | rs1 |
| api2 | bookcatalog2 | mongo2a | mongo2b | mongo2arb | rs2 |
| api3 | bookcatalog3 | mongo3a | mongo3b | mongo3arb | rs3 |

Fiecare Secondary păstrează o copie a bazei din perechea sa. Arbiterul votează la alegerea unui nou Primary și nu stochează cărți. Dacă Primary-ul inițial cade, Secondary-ul poate prelua rolul. Serviciul `sync` se conectează la toate cele trei replica set-uri și propagă schimbările între bazele independente, aproximativ o dată pe secundă. Ștergerile sunt păstrate intern ca marcaje pentru a putea fi propagate. Toate cele trei baze ajung la același set de cărți.

Aceasta este distribuire și sincronizare la nivelul aplicației. Sharding MongoDB nativ necesită `mongos` și config servers; nu este implementat aici. Toate containerele rulează pe același calculator, astfel că testul de failover acoperă căderea unui container, nu defectarea calculatorului.

## Pornire și oprire

Din `D:\riderProjects\BookCatalog`:

```powershell
docker compose up --build -d --remove-orphans
docker compose --profile seed run --rm seed
```

Comanda `seed` adaugă trei cărți demonstrative în prima bază; serviciul `sync` le copiază în celelalte două. Scriptul poate fi rulat din nou fără dubluri și nu suprascrie cărți existente. Folosește volume noi pentru cele trei replica set-uri; volumele MongoDB din configurațiile anterioare rămân pe disc.

Gateway-ul este la `http://localhost:8080`, cărțile la `http://localhost:8080/api/books`, iar OpenAPI la `http://localhost:8080/openapi/v1.json`. Pentru test, fiecare API răspunde direct pe `localhost:18081`, `18082` și `18083`. MongoDB expune local nodurile inițiale pe porturile `27021`, `27022` și `27023`. Nginx distribuie cererile round robin și folosește numele serviciilor Docker pentru descoperirea instanțelor.

Pentru oprire:

```powershell
docker compose down
```

Nu adăuga `-v`: volumele păstrează datele MongoDB după oprire.

## Redis și resurse

Fiecare API folosește Redis pentru cărțile citite după ID. Intrările expiră după 60 de secunde și sunt invalidate la modificare, ștergere sau propagare prin `sync`. Cheile includ numele bazei, astfel încât cache-ul celor trei API-uri rămâne separat. Redis folosește cel mult 32 MB pentru datele din cache, iar containerul are limita de 128 MB. Persistența Redis este dezactivată, deoarece cărțile sunt în MongoDB.

Cele șase noduri MongoDB care păstrează date, cele trei arbitere și serviciile API consumă mai multă memorie decât configurația anterioară. Fișierul Compose include limite de memorie; verifică utilizarea reală cu `docker stats --no-stream`. Serviciul `sync` parcurge toate cărțile la fiecare ciclu, ceea ce este potrivit pentru setul mic de date al laboratorului.

Fiecare API folosește `w=1` pentru ca scrierile să poată continua după căderea Primary-ului, când rămân Secondary-ul și arbiterul. O scriere confirmată cu `w=1` se poate pierde dacă noul Primary cade înainte să fie replicată. Pentru garanții mai puternice sunt necesare trei noduri care păstrează date în fiecare replica set și `w=majority`.

## Testare

```powershell
.\scripts\test-cluster.ps1
.\scripts\test-crud.ps1
.\scripts\test-gateway.ps1
.\scripts\test-concurrency.ps1
```

`test-cluster.ps1` creează o carte prin API 1, o modifică prin API 2, o șterge prin API 3 și așteaptă sincronizarea celor trei baze. `test-crud.ps1` verifică întregul CRUD direct pe API 1. `test-gateway.ps1` verifică distribuirea cererilor către toate cele trei API-uri. `test-concurrency.ps1` trimite cereri CRUD în paralel și așteaptă convergența bazelor între etape. Testele lasă numai marcajele interne necesare propagării ștergerilor.

Pentru a vedea membrii primului replica set:

```powershell
docker compose exec -T mongo1a mongosh --quiet --eval "rs.status().members.map(m => m.name + ': ' + m.stateStr).join(', ')"
```

Pentru failover, când `mongo1a` este Primary, rulează `docker compose stop mongo1a`, așteaptă alegerea noului Primary și rulează `.\scripts\test-cluster.ps1`. Repornește nodul cu `docker compose start mongo1a` și verifică din nou starea membrilor. Repetă separat pentru `mongo2a` și `mongo3a`.

## Endpointuri

| Metodă | Cale | Rezultat |
| --- | --- | --- |
| GET | `/api/books` | Lista cărților active |
| GET | `/api/books/{id}` | O carte sau `404` |
| POST | `/api/books` | Creează o carte și întoarce `201` |
| PUT | `/api/books/{id}` | Înlocuiește o carte sau întoarce `404` |
| DELETE | `/api/books/{id}` | Șterge o carte și întoarce `204` |

Corpul pentru POST și PUT conține `title`, `author`, `publishedYear`, opțional `description` și `tags`. Exemplele sunt în `BookCatalog.Api/BookCatalog.Api.http`.

Pentru eliberarea cache-ului vechi de build Docker, folosește `.\scripts\clear-docker-cache.ps1`. Scriptul curăță numai cache-ul de build nefolosit și păstrează volumele cu date.
