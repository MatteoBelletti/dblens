-- Seed data for dblens_demo (SQL Server)
-- Deterministic data — test assertions can rely on exact row counts:
--   categories: 4, users: 20, products: 30, orders: 40, order_items: 80, payments: 35, audit_logs: 100

USE dblens_demo;
GO

-- Categories (4, one subcategory)
SET IDENTITY_INSERT categories ON;
INSERT INTO categories (id, name, parent_id) VALUES
    (1, N'Elettronica',   NULL),
    (2, N'Abbigliamento', NULL),
    (3, N'Casa e Cucina', NULL),
    (4, N'Smartphone',    1);
SET IDENTITY_INSERT categories OFF;
GO

-- Users (20, realistic Italian data with PII in plaintext — intentional)
SET IDENTITY_INSERT users ON;
INSERT INTO users (id, email, password_hash, first_name, last_name, phone, date_of_birth, fiscal_code, created_at, updated_at) VALUES
    (1,  N'mario.rossi@example.com',       N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1', N'Mario',     N'Rossi',    N'+39 02 12345678',  '1980-01-01', N'RSSMRA80A01H501U', '2023-01-10 08:00:00', '2023-01-10 08:00:00'),
    (2,  N'giuseppe.bianchi@example.com',  N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2', N'Giuseppe',  N'Bianchi',  N'+39 06 23456789',  '1975-02-15', N'BNCGPP75B15F205X', '2023-01-11 09:00:00', '2023-01-11 09:00:00'),
    (3,  N'antonio.verdi@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3', N'Antonio',   N'Verdi',    N'+39 011 34567890', '1982-03-20', N'VRDNTN82C20L219K', '2023-01-12 10:00:00', '2023-01-12 10:00:00'),
    (4,  N'francesca.ferrari@example.com', N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4', N'Francesca', N'Ferrari',  N'+39 055 45678901', '1990-04-04', N'FRRFNC90D44F839P', '2023-01-13 11:00:00', '2023-01-13 11:00:00'),
    (5,  N'luca.esposito@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5', N'Luca',      N'Esposito', N'+39 081 56789012', '1985-05-10', N'SPSLCU85E10F839R', '2023-01-14 12:00:00', '2023-01-14 12:00:00'),
    (6,  N'alessia.romano@example.com',    N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa6', N'Alessia',   N'Romano',   N'+39 02 67890123',  '1992-06-05', N'RMNLSS92F45H501V', '2023-01-15 13:00:00', '2023-01-15 13:00:00'),
    (7,  N'marco.colombo@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa7', N'Marco',     N'Colombo',  N'+39 02 78901234',  '1978-07-01', N'CLMMRC78G01F205T', '2023-01-16 14:00:00', '2023-01-16 14:00:00'),
    (8,  N'valentina.ricci@example.com',   N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa8', N'Valentina', N'Ricci',    N'+39 06 89012345',  '1995-08-06', N'RCCVNT95H46L219E', '2023-01-17 15:00:00', '2023-01-17 15:00:00'),
    (9,  N'andrea.marino@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa9', N'Andrea',    N'Marino',   N'+39 011 90123456', '1988-09-15', N'MRNNDR88I15H501Z', '2023-01-18 16:00:00', '2023-01-18 16:00:00'),
    (10, N'chiara.greco@example.com',      N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa10', N'Chiara',    N'Greco',    N'+39 055 01234567', '1991-10-04', N'GRCCHR91L44F839N', '2023-01-19 17:00:00', '2023-01-19 17:00:00'),
    (11, N'davide.bruno@example.com',      N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa11', N'Davide',    N'Bruno',    N'+39 081 12345670', '1983-11-01', N'BRNDVD83M01H501B', '2023-01-20 08:00:00', '2023-01-20 08:00:00'),
    (12, N'elena.gallo@example.com',       N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa12', N'Elena',     N'Gallo',    N'+39 02 23456781',  '1987-12-02', N'GLLLNE87P42H501W', '2023-01-21 09:00:00', '2023-01-21 09:00:00'),
    (13, N'roberto.conti@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa13', N'Roberto',   N'Conti',    N'+39 06 34567892',  '1970-01-01', N'CNTRRT70A01H501A', '2023-01-22 10:00:00', '2023-01-22 10:00:00'),
    (14, N'silvia.mancini@example.com',    N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa14', N'Silvia',    N'Mancini',  N'+39 011 45678903', '1986-02-04', N'MNCSLV86B44F205C', '2023-01-23 11:00:00', '2023-01-23 11:00:00'),
    (15, N'federico.lombardi@example.com', N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa15', N'Federico',  N'Lombardi', N'+39 055 56789014', '1993-03-01', N'LMBFDR93C01L219M', '2023-01-24 12:00:00', '2023-01-24 12:00:00'),
    (16, N'paola.costa@example.com',       N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa16', N'Paola',     N'Costa',    N'+39 081 67890125', '1979-04-02', N'CSTPLA79D42H501D', '2023-01-25 13:00:00', '2023-01-25 13:00:00'),
    (17, N'matteo.fontana@example.com',    N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa17', N'Matteo',    N'Fontana',  N'+39 02 78901236',  '1994-05-01', N'FNTMTT94E01F839S', '2023-01-26 14:00:00', '2023-01-26 14:00:00'),
    (18, N'sara.barbieri@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa18', N'Sara',      N'Barbieri', N'+39 06 89012347',  '1989-06-02', N'BRBSRA89F42F205L', '2023-01-27 15:00:00', '2023-01-27 15:00:00'),
    (19, N'giovanni.rizzo@example.com',    N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa19', N'Giovanni',  N'Rizzo',    N'+39 011 90123458', '1976-07-15', N'RZZGNN76G15H501F', '2023-01-28 16:00:00', '2023-01-28 16:00:00'),
    (20, N'laura.orlando@example.com',     N'$2a$12$placeholderhashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa20', N'Laura',     N'Orlando',  N'+39 055 01234569', '1997-08-02', N'RLDLRA97H42L219G', '2023-01-29 17:00:00', '2023-01-29 17:00:00');
SET IDENTITY_INSERT users OFF;
GO

-- Addresses (20)
SET IDENTITY_INSERT addresses ON;
INSERT INTO addresses (id, user_id, street, city, postal_code, country, is_default) VALUES
    (1,  1,  N'Via Roma 1',            N'Milano',  N'20100', N'IT', 1),
    (2,  2,  N'Via Nazionale 15',      N'Roma',    N'00100', N'IT', 1),
    (3,  3,  N'Corso Vittorio 20',     N'Torino',  N'10100', N'IT', 1),
    (4,  4,  N'Lungarno Corsini 5',    N'Firenze', N'50100', N'IT', 1),
    (5,  5,  N'Via Toledo 30',         N'Napoli',  N'80100', N'IT', 1),
    (6,  6,  N'Via Manzoni 8',         N'Milano',  N'20121', N'IT', 1),
    (7,  7,  N'Via Garibaldi 12',      N'Milano',  N'20121', N'IT', 1),
    (8,  8,  N'Via Veneto 45',         N'Roma',    N'00187', N'IT', 1),
    (9,  9,  N'Via Po 3',              N'Torino',  N'10124', N'IT', 1),
    (10, 10, N'Piazza Duomo 2',        N'Firenze', N'50122', N'IT', 1),
    (11, 11, N'Via Caracciolo 10',     N'Napoli',  N'80122', N'IT', 1),
    (12, 12, N'Corso Buenos Aires 40', N'Milano',  N'20124', N'IT', 1),
    (13, 13, N'Via del Corso 50',      N'Roma',    N'00186', N'IT', 1),
    (14, 14, N'Via Lagrange 7',        N'Torino',  N'10123', N'IT', 1),
    (15, 15, N'Via Calzaiuoli 9',      N'Firenze', N'50122', N'IT', 1),
    (16, 16, N'Via Chiaia 100',        N'Napoli',  N'80121', N'IT', 1),
    (17, 17, N'Via Montenapoleone 3',  N'Milano',  N'20121', N'IT', 1),
    (18, 18, N'Piazza Navona 1',       N'Roma',    N'00186', N'IT', 1),
    (19, 19, N'Via Cernaia 2',         N'Torino',  N'10121', N'IT', 1),
    (20, 20, N'Via Strozzi 4',         N'Firenze', N'50123', N'IT', 1);
SET IDENTITY_INSERT addresses OFF;
GO

-- Products (30)
SET IDENTITY_INSERT products ON;
INSERT INTO products (id, name, description, price, stock, category_id, created_at) VALUES
    (1,  N'iPhone 15 Pro',            N'Smartphone Apple 256GB',                1299.00, 50,  4, '2023-02-01 08:00:00'),
    (2,  N'Samsung Galaxy S24',       N'Smartphone Samsung 128GB',               899.00, 80,  4, '2023-02-01 08:00:00'),
    (3,  N'Xiaomi 14',                N'Smartphone Xiaomi 256GB',                699.00, 60,  4, '2023-02-01 08:00:00'),
    (4,  N'MacBook Air M3',           N'Laptop Apple 13 pollici 16GB RAM',      1499.00, 30,  1, '2023-02-02 08:00:00'),
    (5,  N'Dell XPS 15',              N'Laptop Dell 15 pollici 32GB RAM',       1799.00, 20,  1, '2023-02-02 08:00:00'),
    (6,  N'Sony WH-1000XM5',          N'Cuffie wireless noise cancelling',        350.00, 100, 1, '2023-02-03 08:00:00'),
    (7,  N'AirPods Pro 2',            N'Auricolari wireless Apple',               279.00, 150, 1, '2023-02-03 08:00:00'),
    (8,  N'iPad Pro 12.9',            N'Tablet Apple 256GB WiFi',               1099.00, 40,  1, '2023-02-04 08:00:00'),
    (9,  N'LG OLED 55"',              N'Smart TV OLED 55 pollici 4K',           1200.00, 15,  1, '2023-02-04 08:00:00'),
    (10, N'Canon EOS R50',            N'Fotocamera mirrorless 24.2 MP',           779.00, 25,  1, '2023-02-05 08:00:00'),
    (11, N'Giacca North Face',        N'Giacca invernale impermeabile taglia M',  299.00, 200, 2, '2023-02-06 08:00:00'),
    (12, N'Sneaker Nike Air Max',     N'Scarpe sportive uomo taglia 42',          159.00, 300, 2, '2023-02-06 08:00:00'),
    (13, N'Jeans Levi 501',           N'Jeans classici uomo 32x32',               89.00, 500, 2, '2023-02-07 08:00:00'),
    (14, N'Maglione Zara',            N'Maglione lana merino taglia S',            49.00, 400, 2, '2023-02-07 08:00:00'),
    (15, N'Borsa Gucci GG',           N'Borsa donna pelle beige',                 950.00, 30,  2, '2023-02-08 08:00:00'),
    (16, N'Vestito Zara Floreale',    N'Vestito estivo taglia S',                  45.00, 350, 2, '2023-02-08 08:00:00'),
    (17, N'Cappotto Massimo Dutti',   N'Cappotto uomo taglia L',                  249.00, 120, 2, '2023-02-09 08:00:00'),
    (18, N'Occhiali Ray-Ban',         N'Occhiali da sole aviator',                159.00, 200, 2, '2023-02-09 08:00:00'),
    (19, N'Set Pentole Lagostina',    N'Set 5 pentole in acciaio inox',            199.00, 80,  3, '2023-02-10 08:00:00'),
    (20, N'Macchina Caffe DeLonghi',  N'Macchina espresso automatica',             449.00, 60,  3, '2023-02-10 08:00:00'),
    (21, N'Robot Cucina Kenwood',     N'Robot da cucina 1500W',                   299.00, 45,  3, '2023-02-11 08:00:00'),
    (22, N'Dyson V15',                N'Aspirapolvere senza filo 60 min',          629.00, 35,  3, '2023-02-11 08:00:00'),
    (23, N'Set Lenzuola Caleffi',     N'Set lenzuola matrimoniale cotone 400tc',    89.00, 200, 3, '2023-02-12 08:00:00'),
    (24, N'Lampada Artemide',         N'Lampada da tavolo design',                 289.00, 50,  3, '2023-02-12 08:00:00'),
    (25, N'Frullatore Vitamix',       N'Frullatore professionale 2.2 litri',       499.00, 30,  3, '2023-02-13 08:00:00'),
    (26, N'Piastre Villeroy Boch',    N'Set piatti da 6 porcellana',               149.00, 100, 3, '2023-02-13 08:00:00'),
    (27, N'Apple Watch Series 9',     N'Smartwatch Apple 45mm GPS',               449.00, 90,  1, '2023-02-14 08:00:00'),
    (28, N'Kindle Paperwhite',        N'E-reader Amazon 8GB',                      149.00, 120, 1, '2023-02-14 08:00:00'),
    (29, N'GoPro Hero 12',            N'Action camera 5.3K',                       379.00, 40,  1, '2023-02-15 08:00:00'),
    (30, N'Nespresso Vertuo Pop',     N'Macchina capsule Nespresso',                99.00, 200, 3, '2023-02-15 08:00:00');
SET IDENTITY_INSERT products OFF;
GO

-- Orders (40)
SET IDENTITY_INSERT orders ON;
INSERT INTO orders (id, user_id, total, status, created_at, updated_at) VALUES
    (1,  1,  1299.00, N'completed',  '2023-03-01 10:00:00', '2023-03-03 10:00:00'),
    (2,  2,   899.00, N'completed',  '2023-03-02 11:00:00', '2023-03-04 11:00:00'),
    (3,  3,  1799.00, N'completed',  '2023-03-03 12:00:00', '2023-03-05 12:00:00'),
    (4,  4,   350.00, N'completed',  '2023-03-04 13:00:00', '2023-03-06 13:00:00'),
    (5,  5,   279.00, N'completed',  '2023-03-05 14:00:00', '2023-03-07 14:00:00'),
    (6,  6,  1499.00, N'completed',  '2023-03-06 15:00:00', '2023-03-08 15:00:00'),
    (7,  7,  1099.00, N'shipped',    '2023-03-07 16:00:00', '2023-03-08 16:00:00'),
    (8,  8,   449.00, N'shipped',    '2023-03-08 17:00:00', '2023-03-09 17:00:00'),
    (9,  9,   299.00, N'processing', '2023-03-09 08:00:00', '2023-03-09 08:00:00'),
    (10, 10,  159.00, N'processing', '2023-03-10 09:00:00', '2023-03-10 09:00:00'),
    (11, 11,  949.00, N'completed',  '2023-03-11 10:00:00', '2023-03-13 10:00:00'),
    (12, 12,  199.00, N'completed',  '2023-03-12 11:00:00', '2023-03-14 11:00:00'),
    (13, 13,  779.00, N'completed',  '2023-03-13 12:00:00', '2023-03-15 12:00:00'),
    (14, 14,   89.00, N'completed',  '2023-03-14 13:00:00', '2023-03-16 13:00:00'),
    (15, 15,  629.00, N'cancelled',  '2023-03-15 14:00:00', '2023-03-16 14:00:00'),
    (16, 16,  289.00, N'completed',  '2023-03-16 15:00:00', '2023-03-18 15:00:00'),
    (17, 17,  499.00, N'shipped',    '2023-03-17 16:00:00', '2023-03-18 16:00:00'),
    (18, 18,  149.00, N'completed',  '2023-03-18 17:00:00', '2023-03-20 17:00:00'),
    (19, 19,  379.00, N'completed',  '2023-03-19 08:00:00', '2023-03-21 08:00:00'),
    (20, 20,   99.00, N'processing', '2023-03-20 09:00:00', '2023-03-20 09:00:00'),
    (21, 1,   279.00, N'completed',  '2023-04-01 10:00:00', '2023-04-03 10:00:00'),
    (22, 2,  1499.00, N'completed',  '2023-04-02 11:00:00', '2023-04-04 11:00:00'),
    (23, 3,   449.00, N'completed',  '2023-04-03 12:00:00', '2023-04-05 12:00:00'),
    (24, 4,   699.00, N'shipped',    '2023-04-04 13:00:00', '2023-04-05 13:00:00'),
    (25, 5,   199.00, N'completed',  '2023-04-05 14:00:00', '2023-04-07 14:00:00'),
    (26, 6,   350.00, N'completed',  '2023-04-06 15:00:00', '2023-04-08 15:00:00'),
    (27, 7,   159.00, N'completed',  '2023-04-07 16:00:00', '2023-04-09 16:00:00'),
    (28, 8,   299.00, N'cancelled',  '2023-04-08 17:00:00', '2023-04-09 17:00:00'),
    (29, 9,   899.00, N'completed',  '2023-04-09 08:00:00', '2023-04-11 08:00:00'),
    (30, 10,  449.00, N'completed',  '2023-04-10 09:00:00', '2023-04-12 09:00:00'),
    (31, 11,  149.00, N'processing', '2023-04-11 10:00:00', '2023-04-11 10:00:00'),
    (32, 12,  629.00, N'completed',  '2023-04-12 11:00:00', '2023-04-14 11:00:00'),
    (33, 13, 1200.00, N'completed',  '2023-04-13 12:00:00', '2023-04-15 12:00:00'),
    (34, 14,  379.00, N'shipped',    '2023-04-14 13:00:00', '2023-04-15 13:00:00'),
    (35, 15,   89.00, N'completed',  '2023-04-15 14:00:00', '2023-04-17 14:00:00'),
    (36, 16,  950.00, N'completed',  '2023-04-16 15:00:00', '2023-04-18 15:00:00'),
    (37, 17,  249.00, N'completed',  '2023-04-17 16:00:00', '2023-04-19 16:00:00'),
    (38, 18, 1299.00, N'shipped',    '2023-04-18 17:00:00', '2023-04-19 17:00:00'),
    (39, 19,   49.00, N'completed',  '2023-04-19 08:00:00', '2023-04-21 08:00:00'),
    (40, 20,  779.00, N'completed',  '2023-04-20 09:00:00', '2023-04-22 09:00:00');
SET IDENTITY_INSERT orders OFF;
GO

-- Order Items (80)
SET IDENTITY_INSERT order_items ON;
INSERT INTO order_items (id, order_id, product_id, quantity, unit_price) VALUES
    (1,  1,  1,  1, 1299.00), (2,  2,  2,  1,  899.00), (3,  3,  5,  1, 1799.00),
    (4,  4,  6,  1,  350.00), (5,  5,  7,  1,  279.00), (6,  6,  4,  1, 1499.00),
    (7,  7,  8,  1, 1099.00), (8,  8,  20, 1,  449.00), (9,  9,  21, 1,  299.00),
    (10, 10, 12, 1,  159.00), (11, 11, 15, 1,  950.00), (12, 12, 19, 1,  199.00),
    (13, 13, 10, 1,  779.00), (14, 14, 13, 1,   89.00), (15, 15, 22, 1,  629.00),
    (16, 16, 24, 1,  289.00), (17, 17, 25, 1,  499.00), (18, 18, 28, 1,  149.00),
    (19, 19, 29, 1,  379.00), (20, 20, 30, 1,   99.00), (21, 21, 7,  1,  279.00),
    (22, 22, 4,  1, 1499.00), (23, 23, 27, 1,  449.00), (24, 24, 3,  1,  699.00),
    (25, 25, 19, 1,  199.00), (26, 26, 6,  1,  350.00), (27, 27, 12, 1,  159.00),
    (28, 28, 21, 1,  299.00), (29, 29, 2,  1,  899.00), (30, 30, 20, 1,  449.00),
    (31, 31, 28, 1,  149.00), (32, 32, 22, 1,  629.00), (33, 33, 9,  1, 1200.00),
    (34, 34, 29, 1,  379.00), (35, 35, 13, 1,   89.00), (36, 36, 15, 1,  950.00),
    (37, 37, 17, 1,  249.00), (38, 38, 1,  1, 1299.00), (39, 39, 14, 1,   49.00),
    (40, 40, 10, 1,  779.00), (41, 1,  7,  1,  279.00), (42, 2,  6,  1,  350.00),
    (43, 3,  6,  1,  350.00), (44, 4,  7,  1,  279.00), (45, 5,  12, 1,  159.00),
    (46, 6,  7,  1,  279.00), (47, 7,  27, 1,  449.00), (48, 8,  27, 1,  449.00),
    (49, 9,  26, 1,  149.00), (50, 10, 14, 1,   49.00), (51, 11, 18, 1,  159.00),
    (52, 12, 26, 1,  149.00), (53, 13, 7,  1,  279.00), (54, 14, 18, 1,  159.00),
    (55, 15, 21, 1,  299.00), (56, 16, 30, 1,   99.00), (57, 17, 30, 1,   99.00),
    (58, 18, 30, 1,   99.00), (59, 19, 28, 1,  149.00), (60, 20, 16, 1,   45.00),
    (61, 21, 14, 1,   49.00), (62, 22, 7,  1,  279.00), (63, 23, 28, 1,  149.00),
    (64, 24, 28, 1,  149.00), (65, 25, 30, 1,   99.00), (66, 26, 28, 1,  149.00),
    (67, 27, 16, 1,   45.00), (68, 28, 30, 1,   99.00), (69, 29, 6,  1,  350.00),
    (70, 30, 30, 1,   99.00), (71, 31, 30, 1,   99.00), (72, 32, 30, 1,   99.00),
    (73, 33, 7,  1,  279.00), (74, 34, 28, 1,  149.00), (75, 35, 16, 1,   45.00),
    (76, 36, 18, 1,  159.00), (77, 37, 13, 1,   89.00), (78, 38, 7,  1,  279.00),
    (79, 39, 30, 1,   99.00), (80, 40, 29, 1,  379.00);
SET IDENTITY_INSERT order_items OFF;
GO

-- Payments (35)
SET IDENTITY_INSERT payments ON;
INSERT INTO payments (id, order_id, amount, card_last4, payment_method, processed_at) VALUES
    (1,  1,  1299.00, N'4242', N'credit_card', '2023-03-01 10:05:00'),
    (2,  2,   899.00, N'1234', N'credit_card', '2023-03-02 11:05:00'),
    (3,  3,  1799.00, N'5678', N'credit_card', '2023-03-03 12:05:00'),
    (4,  4,   350.00, N'9012', N'paypal',      '2023-03-04 13:05:00'),
    (5,  5,   279.00, N'3456', N'credit_card', '2023-03-05 14:05:00'),
    (6,  6,  1499.00, N'7890', N'credit_card', '2023-03-06 15:05:00'),
    (7,  7,  1099.00, N'2345', N'credit_card', '2023-03-07 16:05:00'),
    (8,  8,   449.00, N'6789', N'paypal',      '2023-03-08 17:05:00'),
    (9,  11,  949.00, N'4321', N'credit_card', '2023-03-11 10:05:00'),
    (10, 12,  199.00, N'8765', N'satispay',    '2023-03-12 11:05:00'),
    (11, 13,  779.00, N'5555', N'credit_card', '2023-03-13 12:05:00'),
    (12, 14,   89.00, N'1111', N'paypal',      '2023-03-14 13:05:00'),
    (13, 16,  289.00, N'2222', N'credit_card', '2023-03-16 15:05:00'),
    (14, 17,  499.00, N'3333', N'credit_card', '2023-03-17 16:05:00'),
    (15, 18,  149.00, N'4444', N'satispay',    '2023-03-18 17:05:00'),
    (16, 19,  379.00, N'5566', N'credit_card', '2023-03-19 08:05:00'),
    (17, 21,  279.00, N'7788', N'paypal',      '2023-04-01 10:05:00'),
    (18, 22, 1499.00, N'9900', N'credit_card', '2023-04-02 11:05:00'),
    (19, 23,  449.00, N'1122', N'credit_card', '2023-04-03 12:05:00'),
    (20, 24,  699.00, N'3344', N'credit_card', '2023-04-04 13:05:00'),
    (21, 25,  199.00, N'5566', N'satispay',    '2023-04-05 14:05:00'),
    (22, 26,  350.00, N'7788', N'paypal',      '2023-04-06 15:05:00'),
    (23, 27,  159.00, N'9900', N'credit_card', '2023-04-07 16:05:00'),
    (24, 29,  899.00, N'1234', N'credit_card', '2023-04-09 08:05:00'),
    (25, 30,  449.00, N'5678', N'paypal',      '2023-04-10 09:05:00'),
    (26, 32,  629.00, N'9012', N'credit_card', '2023-04-12 11:05:00'),
    (27, 33, 1200.00, N'3456', N'credit_card', '2023-04-13 12:05:00'),
    (28, 34,  379.00, N'7890', N'satispay',    '2023-04-14 13:05:00'),
    (29, 35,   89.00, N'2345', N'paypal',      '2023-04-15 14:05:00'),
    (30, 36,  950.00, N'6789', N'credit_card', '2023-04-16 15:05:00'),
    (31, 37,  249.00, N'4321', N'credit_card', '2023-04-17 16:05:00'),
    (32, 38, 1299.00, N'8765', N'credit_card', '2023-04-18 17:05:00'),
    (33, 39,   49.00, N'5555', N'satispay',    '2023-04-19 08:05:00'),
    (34, 40,  779.00, N'1111', N'credit_card', '2023-04-20 09:05:00'),
    (35, 1,   279.00, N'4242', N'credit_card', '2023-03-01 10:10:00');
SET IDENTITY_INSERT payments OFF;
GO

-- Audit Logs (100)
SET IDENTITY_INSERT audit_logs ON;
INSERT INTO audit_logs (id, user_id, action, entity_type, entity_id, ip_address, created_at) VALUES
    (1,   1,  N'login',        N'user',    1,  N'93.56.12.1',   '2023-03-01 09:55:00'),
    (2,   1,  N'create_order', N'order',   1,  N'93.56.12.1',   '2023-03-01 10:00:00'),
    (3,   1,  N'pay_order',    N'payment', 1,  N'93.56.12.1',   '2023-03-01 10:05:00'),
    (4,   1,  N'logout',       N'user',    1,  N'93.56.12.1',   '2023-03-01 10:30:00'),
    (5,   2,  N'login',        N'user',    2,  N'151.38.45.22', '2023-03-02 10:55:00'),
    (6,   2,  N'create_order', N'order',   2,  N'151.38.45.22', '2023-03-02 11:00:00'),
    (7,   2,  N'pay_order',    N'payment', 2,  N'151.38.45.22', '2023-03-02 11:05:00'),
    (8,   2,  N'logout',       N'user',    2,  N'151.38.45.22', '2023-03-02 11:30:00'),
    (9,   3,  N'login',        N'user',    3,  N'2.225.100.50', '2023-03-03 11:55:00'),
    (10,  3,  N'create_order', N'order',   3,  N'2.225.100.50', '2023-03-03 12:00:00'),
    (11,  3,  N'pay_order',    N'payment', 3,  N'2.225.100.50', '2023-03-03 12:05:00'),
    (12,  3,  N'logout',       N'user',    3,  N'2.225.100.50', '2023-03-03 12:30:00'),
    (13,  4,  N'login',        N'user',    4,  N'79.22.33.44',  '2023-03-04 12:55:00'),
    (14,  4,  N'create_order', N'order',   4,  N'79.22.33.44',  '2023-03-04 13:00:00'),
    (15,  4,  N'pay_order',    N'payment', 4,  N'79.22.33.44',  '2023-03-04 13:05:00'),
    (16,  5,  N'login',        N'user',    5,  N'82.56.70.100', '2023-03-05 13:55:00'),
    (17,  5,  N'create_order', N'order',   5,  N'82.56.70.100', '2023-03-05 14:00:00'),
    (18,  5,  N'pay_order',    N'payment', 5,  N'82.56.70.100', '2023-03-05 14:05:00'),
    (19,  6,  N'login',        N'user',    6,  N'93.56.12.2',   '2023-03-06 14:55:00'),
    (20,  6,  N'create_order', N'order',   6,  N'93.56.12.2',   '2023-03-06 15:00:00'),
    (21,  6,  N'pay_order',    N'payment', 6,  N'93.56.12.2',   '2023-03-06 15:05:00'),
    (22,  7,  N'login',        N'user',    7,  N'151.38.45.23', '2023-03-07 15:55:00'),
    (23,  7,  N'create_order', N'order',   7,  N'151.38.45.23', '2023-03-07 16:00:00'),
    (24,  7,  N'pay_order',    N'payment', 7,  N'151.38.45.23', '2023-03-07 16:05:00'),
    (25,  8,  N'login',        N'user',    8,  N'2.225.100.51', '2023-03-08 16:55:00'),
    (26,  8,  N'create_order', N'order',   8,  N'2.225.100.51', '2023-03-08 17:00:00'),
    (27,  8,  N'pay_order',    N'payment', 8,  N'2.225.100.51', '2023-03-08 17:05:00'),
    (28,  9,  N'login',        N'user',    9,  N'79.22.33.45',  '2023-03-09 07:55:00'),
    (29,  9,  N'create_order', N'order',   9,  N'79.22.33.45',  '2023-03-09 08:00:00'),
    (30,  10, N'login',        N'user',    10, N'82.56.70.101', '2023-03-10 08:55:00'),
    (31,  10, N'create_order', N'order',   10, N'82.56.70.101', '2023-03-10 09:00:00'),
    (32,  11, N'login',        N'user',    11, N'93.56.12.3',   '2023-03-11 09:55:00'),
    (33,  11, N'create_order', N'order',   11, N'93.56.12.3',   '2023-03-11 10:00:00'),
    (34,  11, N'pay_order',    N'payment', 9,  N'93.56.12.3',   '2023-03-11 10:05:00'),
    (35,  12, N'login',        N'user',    12, N'151.38.45.24', '2023-03-12 10:55:00'),
    (36,  12, N'create_order', N'order',   12, N'151.38.45.24', '2023-03-12 11:00:00'),
    (37,  12, N'pay_order',    N'payment', 10, N'151.38.45.24', '2023-03-12 11:05:00'),
    (38,  13, N'login',        N'user',    13, N'2.225.100.52', '2023-03-13 11:55:00'),
    (39,  13, N'create_order', N'order',   13, N'2.225.100.52', '2023-03-13 12:00:00'),
    (40,  13, N'pay_order',    N'payment', 11, N'2.225.100.52', '2023-03-13 12:05:00'),
    (41,  14, N'login',        N'user',    14, N'79.22.33.46',  '2023-03-14 12:55:00'),
    (42,  14, N'create_order', N'order',   14, N'79.22.33.46',  '2023-03-14 13:00:00'),
    (43,  14, N'pay_order',    N'payment', 12, N'79.22.33.46',  '2023-03-14 13:05:00'),
    (44,  15, N'login',        N'user',    15, N'82.56.70.102', '2023-03-15 13:55:00'),
    (45,  15, N'create_order', N'order',   15, N'82.56.70.102', '2023-03-15 14:00:00'),
    (46,  15, N'cancel_order', N'order',   15, N'82.56.70.102', '2023-03-16 09:00:00'),
    (47,  16, N'login',        N'user',    16, N'93.56.12.4',   '2023-03-16 14:55:00'),
    (48,  16, N'create_order', N'order',   16, N'93.56.12.4',   '2023-03-16 15:00:00'),
    (49,  16, N'pay_order',    N'payment', 13, N'93.56.12.4',   '2023-03-16 15:05:00'),
    (50,  17, N'login',        N'user',    17, N'151.38.45.25', '2023-03-17 15:55:00'),
    (51,  17, N'create_order', N'order',   17, N'151.38.45.25', '2023-03-17 16:00:00'),
    (52,  17, N'pay_order',    N'payment', 14, N'151.38.45.25', '2023-03-17 16:05:00'),
    (53,  18, N'login',        N'user',    18, N'2.225.100.53', '2023-03-18 16:55:00'),
    (54,  18, N'create_order', N'order',   18, N'2.225.100.53', '2023-03-18 17:00:00'),
    (55,  18, N'pay_order',    N'payment', 15, N'2.225.100.53', '2023-03-18 17:05:00'),
    (56,  19, N'login',        N'user',    19, N'79.22.33.47',  '2023-03-19 07:55:00'),
    (57,  19, N'create_order', N'order',   19, N'79.22.33.47',  '2023-03-19 08:00:00'),
    (58,  19, N'pay_order',    N'payment', 16, N'79.22.33.47',  '2023-03-19 08:05:00'),
    (59,  20, N'login',        N'user',    20, N'82.56.70.103', '2023-03-20 08:55:00'),
    (60,  20, N'create_order', N'order',   20, N'82.56.70.103', '2023-03-20 09:00:00'),
    (61,  1,  N'login',        N'user',    1,  N'93.56.12.1',   '2023-04-01 09:55:00'),
    (62,  1,  N'create_order', N'order',   21, N'93.56.12.1',   '2023-04-01 10:00:00'),
    (63,  1,  N'pay_order',    N'payment', 17, N'93.56.12.1',   '2023-04-01 10:05:00'),
    (64,  2,  N'login',        N'user',    2,  N'151.38.45.22', '2023-04-02 10:55:00'),
    (65,  2,  N'create_order', N'order',   22, N'151.38.45.22', '2023-04-02 11:00:00'),
    (66,  2,  N'pay_order',    N'payment', 18, N'151.38.45.22', '2023-04-02 11:05:00'),
    (67,  3,  N'login',        N'user',    3,  N'2.225.100.50', '2023-04-03 11:55:00'),
    (68,  3,  N'create_order', N'order',   23, N'2.225.100.50', '2023-04-03 12:00:00'),
    (69,  4,  N'login',        N'user',    4,  N'79.22.33.44',  '2023-04-04 12:55:00'),
    (70,  4,  N'create_order', N'order',   24, N'79.22.33.44',  '2023-04-04 13:00:00'),
    (71,  5,  N'login',        N'user',    5,  N'82.56.70.100', '2023-04-05 13:55:00'),
    (72,  5,  N'create_order', N'order',   25, N'82.56.70.100', '2023-04-05 14:00:00'),
    (73,  5,  N'pay_order',    N'payment', 21, N'82.56.70.100', '2023-04-05 14:05:00'),
    (74,  6,  N'login',        N'user',    6,  N'93.56.12.2',   '2023-04-06 14:55:00'),
    (75,  6,  N'create_order', N'order',   26, N'93.56.12.2',   '2023-04-06 15:00:00'),
    (76,  6,  N'pay_order',    N'payment', 22, N'93.56.12.2',   '2023-04-06 15:05:00'),
    (77,  7,  N'login',        N'user',    7,  N'151.38.45.23', '2023-04-07 15:55:00'),
    (78,  7,  N'create_order', N'order',   27, N'151.38.45.23', '2023-04-07 16:00:00'),
    (79,  7,  N'pay_order',    N'payment', 23, N'151.38.45.23', '2023-04-07 16:05:00'),
    (80,  8,  N'login',        N'user',    8,  N'2.225.100.51', '2023-04-08 16:55:00'),
    (81,  8,  N'create_order', N'order',   28, N'2.225.100.51', '2023-04-08 17:00:00'),
    (82,  8,  N'cancel_order', N'order',   28, N'2.225.100.51', '2023-04-09 09:00:00'),
    (83,  9,  N'login',        N'user',    9,  N'79.22.33.45',  '2023-04-09 07:55:00'),
    (84,  9,  N'create_order', N'order',   29, N'79.22.33.45',  '2023-04-09 08:00:00'),
    (85,  9,  N'pay_order',    N'payment', 24, N'79.22.33.45',  '2023-04-09 08:05:00'),
    (86,  10, N'login',        N'user',    10, N'82.56.70.101', '2023-04-10 08:55:00'),
    (87,  10, N'create_order', N'order',   30, N'82.56.70.101', '2023-04-10 09:00:00'),
    (88,  10, N'pay_order',    N'payment', 25, N'82.56.70.101', '2023-04-10 09:05:00'),
    (89,  11, N'login',        N'user',    11, N'93.56.12.3',   '2023-04-11 09:55:00'),
    (90,  11, N'create_order', N'order',   31, N'93.56.12.3',   '2023-04-11 10:00:00'),
    (91,  12, N'login',        N'user',    12, N'151.38.45.24', '2023-04-12 10:55:00'),
    (92,  12, N'create_order', N'order',   32, N'151.38.45.24', '2023-04-12 11:00:00'),
    (93,  12, N'pay_order',    N'payment', 26, N'151.38.45.24', '2023-04-12 11:05:00'),
    (94,  13, N'login',        N'user',    13, N'2.225.100.52', '2023-04-13 11:55:00'),
    (95,  13, N'create_order', N'order',   33, N'2.225.100.52', '2023-04-13 12:00:00'),
    (96,  13, N'pay_order',    N'payment', 27, N'2.225.100.52', '2023-04-13 12:05:00'),
    (97,  14, N'login',        N'user',    14, N'79.22.33.46',  '2023-04-14 12:55:00'),
    (98,  14, N'create_order', N'order',   34, N'79.22.33.46',  '2023-04-14 13:00:00'),
    (99,  15, N'login',        N'user',    15, N'82.56.70.102', '2023-04-15 13:55:00'),
    (100, 15, N'create_order', N'order',   35, N'82.56.70.102', '2023-04-15 14:00:00');
SET IDENTITY_INSERT audit_logs OFF;
GO

-- Item Reviews (20 — reviews for the first 20 order_items; deepens cascade chain to depth 3 from users)
SET IDENTITY_INSERT item_reviews ON;
INSERT INTO item_reviews (id, order_item_id, rating, review_text, created_at) VALUES
    (1,  1,  5, N'Ottimo prodotto!',            '2023-03-10 08:00:00'),
    (2,  2,  4, N'Buon rapporto qualità/prezzo', '2023-03-10 09:00:00'),
    (3,  3,  5, N'Arrivato in anticipo',         '2023-03-10 10:00:00'),
    (4,  4,  3, N'Nella media',                  '2023-03-10 11:00:00'),
    (5,  5,  5, N'Consigliatissimo',             '2023-03-10 12:00:00'),
    (6,  6,  4, N'Soddisfatto',                  '2023-03-10 13:00:00'),
    (7,  7,  5, N'Perfetto',                     '2023-03-10 14:00:00'),
    (8,  8,  2, N'Poteva essere meglio',          '2023-03-10 15:00:00'),
    (9,  9,  4, N'Buono',                        '2023-03-10 16:00:00'),
    (10, 10, 5, N'Eccellente',                   '2023-03-10 17:00:00'),
    (11, 11, 3, N'Nella norma',                  '2023-03-11 08:00:00'),
    (12, 12, 5, N'Fantastico',                   '2023-03-11 09:00:00'),
    (13, 13, 4, N'Ottima qualità',               '2023-03-11 10:00:00'),
    (14, 14, 5, N'Molto soddisfatto',             '2023-03-11 11:00:00'),
    (15, 15, 1, N'Deludente',                    '2023-03-11 12:00:00'),
    (16, 16, 5, N'Perfetto come descritto',      '2023-03-11 13:00:00'),
    (17, 17, 4, N'Buona spedizione',             '2023-03-11 14:00:00'),
    (18, 18, 5, N'Consigliatissimo',             '2023-03-11 15:00:00'),
    (19, 19, 3, N'Mediocre',                     '2023-03-11 16:00:00'),
    (20, 20, 4, N'Buono',                        '2023-03-11 17:00:00');
SET IDENTITY_INSERT item_reviews OFF;
GO

-- demo_good seed (minimal rows to ensure SQL Server allocates pages, needed for size tests)
SET IDENTITY_INSERT demo_good.departments ON;
INSERT INTO demo_good.departments (id, name) VALUES (1, N'Engineering'), (2, N'Design');
SET IDENTITY_INSERT demo_good.departments OFF;
GO

SET IDENTITY_INSERT demo_good.employees ON;
INSERT INTO demo_good.employees (id, department_id, name, email) VALUES
    (1, 1, N'Alice Rossi',  N'alice@example.com'),
    (2, 2, N'Bob Verdi',    N'bob@example.com');
SET IDENTITY_INSERT demo_good.employees OFF;
GO

SET IDENTITY_INSERT demo_good.projects ON;
INSERT INTO demo_good.projects (id, owner_id, name) VALUES
    (1, 1, N'Project Alpha'),
    (2, 2, N'Project Beta');
SET IDENTITY_INSERT demo_good.projects OFF;
GO
