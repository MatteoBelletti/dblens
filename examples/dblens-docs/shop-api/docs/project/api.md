# API

Endpoints declared in the code.

<!-- dblens:item endpoint:GET /health hash=b1687460 gen=32be554c -->
### GET /health

| Verb | Route | Handler | Declared as | File |
| --- | --- | --- | --- | --- |
| GET | /health | (none) | minimal | Program.cs |
<!-- dblens:slot endpoint:GET /health purpose max-lines=2 evidence=human hash=b1687460 text=87a93bb4 -->
Liveness probe: answers 200 OK with no body and reads nothing.
<!-- dblens:end -->

<!-- dblens:item endpoint:POST /orders hash=9663ff4d gen=928515d5 -->
### POST /orders

| Verb | Route | Handler | Declared as | File |
| --- | --- | --- | --- | --- |
| POST | /orders | (none) | minimal | Program.cs |
<!-- dblens:slot endpoint:POST /orders purpose max-lines=2 evidence=inferred hash=9663ff4d text=140ce3a9 -->
Creates an order from the request's customer and total through the order store. Responds 201 Created with the new order and a Location of /orders/{id}.
<!-- dblens:end -->

<!-- dblens:item endpoint:DELETE /orders/{id} hash=04d6d926 gen=4c152229 -->
### DELETE /orders/{id}

| Verb | Route | Handler | Declared as | File |
| --- | --- | --- | --- | --- |
| DELETE | /orders/{id} | (none) | minimal | Program.cs |
<!-- dblens:slot endpoint:DELETE /orders/{id} purpose max-lines=2 evidence=inferred hash=04d6d926 text=8e7c6121 -->
Cancels an order by setting its status to "Cancelled" rather than removing it. Responds 204 No Content, or 404 Not Found when no order has the id. It does not check the caller's customer.
<!-- dblens:end -->

<!-- dblens:item endpoint:GET /orders/{id} hash=ef099a33 gen=457539b6 -->
### GET /orders/{id}

| Verb | Route | Handler | Declared as | File |
| --- | --- | --- | --- | --- |
| GET | /orders/{id} | (none) | minimal | Program.cs |
<!-- dblens:slot endpoint:GET /orders/{id} purpose max-lines=2 evidence=inferred hash=ef099a33 text=b9ea80eb -->
Returns an order by id, only when its customer equals the X-Customer request header. Responds 404 Not Found when the order does not exist or belongs to another customer.
<!-- dblens:end -->
