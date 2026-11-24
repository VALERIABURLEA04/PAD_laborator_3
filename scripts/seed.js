const books = db.getSiblingDB(process.env.SEED_DATABASE || "bookcatalog").books;

const sampleBooks = [
  {
    _id: ObjectId("671000000000000000000001"),
    Title: "The Hobbit",
    Author: "J. R. R. Tolkien",
    PublishedYear: 1937,
    Description: "A fantasy adventure through Middle-earth.",
    Tags: ["fantasy", "adventure"]
  },
  {
    _id: ObjectId("671000000000000000000002"),
    Title: "Dune",
    Author: "Frank Herbert",
    PublishedYear: 1965,
    Description: "A science fiction story set on Arrakis.",
    Tags: ["science fiction"]
  },
  {
    _id: ObjectId("671000000000000000000003"),
    Title: "1984",
    Author: "George Orwell",
    PublishedYear: 1949,
    Description: "A dystopian novel about surveillance and control.",
    Tags: ["dystopia", "classic"]
  }
];

for (const book of sampleBooks) {
  const { _id, ...details } = book;
  books.updateOne({ _id }, { $setOnInsert: details }, { upsert: true });
}

print(`Seed complete. Active books in collection: ${books.countDocuments({ IsDeleted: { $ne: true } })}`);
